namespace DL_Skin_Randomiser.Models
{
    public class UpdateCheckResult
    {
        public bool UpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string ReleaseUrl { get; set; } = "";
        public string ReleaseName { get; set; } = "";
        public string InstallerName { get; set; } = "";
        public string InstallerDownloadUrl { get; set; } = "";
        public bool HasInstaller => !string.IsNullOrWhiteSpace(InstallerDownloadUrl);
    }
}
