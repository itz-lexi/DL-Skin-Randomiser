namespace DL_Skin_Randomiser.Models
{
    public class RepairPreservationResult
    {
        public string BackupDirectory { get; set; } = "";
        public string DlmmLaunchSettingsPath { get; set; } = "";
        public int DlmmLaunchSettingCount { get; set; }
        public int GameInfoBackupCount { get; set; }
        public List<string> GameInfoBackupPaths { get; set; } = [];
    }
}
