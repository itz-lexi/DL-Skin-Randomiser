using System.IO;
using System.Net.Http;
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
            var installerName = "";
            var installerDownloadUrl = "";

            if (root.TryGetProperty("assets", out var assetsElement))
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var assetName = asset.GetProperty("name").GetString() ?? "";
                    if (!assetName.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    installerName = assetName;
                    installerDownloadUrl = asset.TryGetProperty("browser_download_url", out var downloadElement)
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
                InstallerName = installerName,
                InstallerDownloadUrl = installerDownloadUrl,
                UpdateAvailable = latestVersion > currentVersion
            };
        }

        public static async Task<string> DownloadInstallerAsync(UpdateCheckResult update)
        {
            if (!update.HasInstaller)
                throw new InvalidOperationException("The latest GitHub release does not include a setup installer.");

            var downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            Directory.CreateDirectory(downloadsPath);

            var destinationPath = Path.Combine(downloadsPath, update.InstallerName);
            using var response = await HttpClient.GetAsync(update.InstallerDownloadUrl);
            response.EnsureSuccessStatusCode();

            await using var sourceStream = await response.Content.ReadAsStreamAsync();
            await using var destinationStream = File.Create(destinationPath);
            await sourceStream.CopyToAsync(destinationStream);

            return destinationPath;
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
