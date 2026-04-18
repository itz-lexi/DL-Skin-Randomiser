using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class UpdateService
    {
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

            var scriptPath = Path.Combine(updateRoot, "apply-update.ps1");
            File.WriteAllText(scriptPath, BuildUpdateScript(
                Environment.ProcessId,
                sourceDirectory,
                targetDirectory,
                currentExePath,
                updateRoot));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\""
            };

            if (!CanWriteToDirectory(targetDirectory))
                startInfo.Verb = "runas";

            Process.Start(startInfo);
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

        private static string BuildUpdateScript(int processId, string sourceDirectory, string targetDirectory, string exePath, string updateRoot)
        {
            return $$"""
                $ErrorActionPreference = 'Stop'
                $processId = {{processId}}
                $sourceDirectory = '{{EscapePowerShell(sourceDirectory)}}'
                $targetDirectory = '{{EscapePowerShell(targetDirectory)}}'
                $exePath = '{{EscapePowerShell(exePath)}}'
                $updateRoot = '{{EscapePowerShell(updateRoot)}}'

                try {
                    Wait-Process -Id $processId -Timeout 60 -ErrorAction SilentlyContinue
                } catch {
                }

                Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $targetDirectory -Recurse -Force
                Start-Process -FilePath $exePath

                Start-Sleep -Seconds 2
                Remove-Item -LiteralPath $updateRoot -Recurse -Force -ErrorAction SilentlyContinue
                """;
        }

        private static string EscapePowerShell(string value)
        {
            return value.Replace("'", "''");
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
