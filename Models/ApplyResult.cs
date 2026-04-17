namespace DL_Skin_Randomiser.Models
{
    public class ApplyResult
    {
        public int WrittenCount { get; set; }
        public int EnabledCount { get; set; }
        public int ForcedDisabledCount { get; set; }
        public int StagedEnabledCount { get; set; }
        public int StagedDisabledCount { get; set; }
        public int StagingSkippedCount { get; set; }
        public bool GameFilesStaged { get; set; }
        public bool RequiresDlmmApply { get; set; }
        public string BackupPath { get; set; } = "";
        public string AddonsBackupPath { get; set; } = "";
    }
}
