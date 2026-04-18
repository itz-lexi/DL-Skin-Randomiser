using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class UpdateService
    {
        private const string ApplyPortableUpdateArgument = "--apply-portable-update";
        private const string LatestReleaseUrl = "https://api.github.com/repos/itz-lexi/DL-Skin-Randomiser/releases/latest";
        public const string ReleasesPageUrl = "https://github.com/itz-lexi/DL-Skin-Randomiser/releases";
        private static readonly HttpClient HttpClient = new();

        static UpdateService()
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DL-Skin-Randomiser");
        }

        public static string CurrentVersion =>
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "0.0.0";

        public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
        {
            using var response = await HttpClient.GetAsync(LatestReleaseUrl);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;

            var latestTag = root.GetProperty("tag_name").GetString() ?? "";
            var releaseName = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? latestTag
                : latestTag;
            var releaseUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString() ?? ""
                : "";
            var portablePackageName = "";
            var portablePackageDownloadUrl = "";

            if (root.TryGetProperty("assets", out var assetsElement))
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var assetName = asset.GetProperty("name").GetString() ?? "";
                    if (!assetName.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    portablePackageName = assetName;
                    portablePackageDownloadUrl = asset.TryGetProperty("browser_download_url", out var downloadElement)
                        ? downloadElement.GetString() ?? ""
                        : "";
                    break;
                }
            }

            var currentVersion = NormalizeVersion(CurrentVersion);
            var latestVersion = NormalizeVersion(latestTag);

            return new UpdateCheckResult
            {
                CurrentVersion = currentVersion.ToString(),
                LatestVersion = latestVersion.ToString(),
                ReleaseName = releaseName,
                ReleaseUrl = releaseUrl,
                PortablePackageName = portablePackageName,
                PortablePackageDownloadUrl = portablePackageDownloadUrl,
                UpdateAvailable = latestVersion > currentVersion
            };
        }

        public static async Task PrepareAndLaunchPortableUpdateAsync(UpdateCheckResult update)
        {
            if (!update.HasPortablePackage)
                throw new InvalidOperationException("The latest GitHub release does not include a portable app package.");

            var currentExePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not find the running app path.");
            var targetDirectory = Path.GetDirectoryName(currentExePath)
                ?? throw new InvalidOperationException("Could not find the app install folder.");

            var updateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DL-Skin-Randomiser",
                "UpdateStaging",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            PruneOldUpdateStaging(Path.GetDirectoryName(updateRoot) ?? "");
            var extractionPath = Path.Combine(updateRoot, "extracted");
            Directory.CreateDirectory(extractionPath);

            var packagePath = Path.Combine(updateRoot, update.PortablePackageName);
            using var response = await HttpClient.GetAsync(update.PortablePackageDownloadUrl);
            response.EnsureSuccessStatusCode();

            await using (var sourceStream = await response.Content.ReadAsStreamAsync())
            await using (var destinationStream = File.Create(packagePath))
            {
                await sourceStream.CopyToAsync(destinationStream);
            }

            ZipFile.ExtractToDirectory(packagePath, extractionPath, overwriteFiles: true);
            var extractedExePath = Directory
                .EnumerateFiles(extractionPath, "DL-Skin-Randomiser.exe", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The portable update did not contain DL-Skin-Randomiser.exe.");
            var sourceDirectory = Path.GetDirectoryName(extractedExePath)
                ?? throw new InvalidOperationException("Could not find the extracted app folder.");

            var startInfo = new ProcessStartInfo
            {
                FileName = extractedExePath,
                UseShellExecute = true,
                WorkingDirectory = sourceDirectory,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(" ", new[]
                {
                    QuoteArgument(ApplyPortableUpdateArgument),
                    QuoteArgument("--process-id"),
                    QuoteArgument(Environment.ProcessId.ToString()),
                    QuoteArgument("--source"),
                    QuoteArgument(sourceDirectory),
                    QuoteArgument("--target"),
                    QuoteArgument(targetDirectory),
                    QuoteArgument("--exe"),
                    QuoteArgument(currentExePath),
                    QuoteArgument("--staging"),
                    QuoteArgument(updateRoot)
                })
            };

            if (!CanWriteToDirectory(targetDirectory))
                startInfo.Verb = "runas";

            var updaterProcess = Process.Start(startInfo);
            if (updaterProcess is null)
                throw new InvalidOperationException("Could not launch the staged updater.");
        }

        public static bool IsPortableUpdateCommand(IReadOnlyList<string> args)
        {
            return args.Any(arg => string.Equals(arg, ApplyPortableUpdateArgument, StringComparison.OrdinalIgnoreCase));
        }

        public static async Task<int> ApplyPortableUpdateFromCommandLineAsync(IReadOnlyList<string> args)
        {
            var options = ParseArguments(args);
            var updateRoot = GetOption(options, "staging");
            var logPath = Path.Combine(updateRoot, "update.log");

            try
            {
                Directory.CreateDirectory(updateRoot);
                await AppendUpdateLogAsync(logPath, "Updater started.");

                var processIdValue = GetOption(options, "process-id");
                if (!int.TryParse(processIdValue, out var processId))
                    throw new InvalidOperationException("The staged updater did not receive a valid process id.");

                var sourceDirectory = GetOption(options, "source");
                var targetDirectory = GetOption(options, "target");
                var exePath = GetOption(options, "exe");

                await WaitForAppExitAsync(processId, logPath);
                await CopyUpdateFilesAsync(sourceDirectory, targetDirectory, logPath);

                await AppendUpdateLogAsync(logPath, $"Restarting app: {exePath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = targetDirectory,
                    UseShellExecute = true
                });

                await AppendUpdateLogAsync(logPath, "Updater finished successfully.");
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    await AppendUpdateLogAsync(logPath, $"Updater failed: {ex}");
                }
                catch
                {
                    // Nothing else can be done if logging also fails.
                }

                MessageBox.Show(
                    $"The update could not be installed.\n\n{ex.Message}\n\nLog: {logPath}",
                    "Update failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return 1;
            }
        }

        private static bool CanWriteToDirectory(string directory)
        {
            try
            {
                var probePath = Path.Combine(directory, $".update-write-test-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probePath, "");
                File.Delete(probePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void PruneOldUpdateStaging(string stagingRoot)
        {
            if (string.IsNullOrWhiteSpace(stagingRoot) || !Directory.Exists(stagingRoot))
                return;

            foreach (var oldDirectory in Directory.GetDirectories(stagingRoot)
                         .Select(path => new DirectoryInfo(path))
                         .OrderByDescending(directory => directory.LastWriteTimeUtc)
                         .Skip(3))
            {
                try
                {
                    oldDirectory.Delete(recursive: true);
                }
                catch
                {
                    // A previous updater may still be cleaning up. Leave locked staging folders alone.
                }
            }
        }

        private static async Task WaitForAppExitAsync(int processId, string logPath)
        {
            await AppendUpdateLogAsync(logPath, $"Waiting for app process {processId} to exit.");

            try
            {
                var process = Process.GetProcessById(processId);
                if (!process.WaitForExit(60000))
                    throw new InvalidOperationException("The app did not exit within 60 seconds, so the update could not safely replace its files.");
            }
            catch (ArgumentException)
            {
                await AppendUpdateLogAsync(logPath, "App process already exited.");
            }
        }

        private static async Task CopyUpdateFilesAsync(string sourceDirectory, string targetDirectory, string logPath)
        {
            if (!Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException($"Update source folder was not found: {sourceDirectory}");

            Directory.CreateDirectory(targetDirectory);
            await AppendUpdateLogAsync(logPath, $"Copying update from {sourceDirectory} to {targetDirectory}.");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            Exception? lastError = null;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                        var targetPath = Path.Combine(targetDirectory, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetDirectory);
                        File.Copy(sourcePath, targetPath, overwrite: true);
                    }

                    await AppendUpdateLogAsync(logPath, "Copied update files.");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastError = ex;
                    await Task.Delay(1000);
                }
            }

            throw new IOException("The update files could not be copied. The app may still be running or the install folder may need administrator permission.", lastError);
        }

        private static Dictionary<string, string> ParseArguments(IReadOnlyList<string> args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < args.Count; index++)
            {
                var arg = args[index];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                    continue;

                var key = arg[2..];
                if (string.Equals(key, ApplyPortableUpdateArgument[2..], StringComparison.OrdinalIgnoreCase))
                    continue;

                if (index + 1 >= args.Count)
                    throw new InvalidOperationException($"Missing value for update argument: {arg}");

                options[key] = args[++index];
            }

            return options;
        }

        private static string GetOption(IReadOnlyDictionary<string, string> options, string name)
        {
            if (options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;

            throw new InvalidOperationException($"Missing update argument: --{name}");
        }

        private static async Task AppendUpdateLogAsync(string logPath, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(logPath, line);
        }

        private static string QuoteArgument(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private static Version NormalizeVersion(string version)
        {
            var cleanVersion = version.Trim();
            if (cleanVersion.StartsWith('v') || cleanVersion.StartsWith('V'))
                cleanVersion = cleanVersion[1..];

            var metadataIndex = cleanVersion.IndexOfAny(['-', '+']);
            if (metadataIndex >= 0)
                cleanVersion = cleanVersion[..metadataIndex];

            return Version.TryParse(cleanVersion, out var parsedVersion)
                ? parsedVersion
                : new Version(0, 0, 0);
        }
    }
}
