namespace DL_Skin_Randomiser.Models
{
    public class StagingRepairResult
    {
        public int RemovedLiveVpkCount { get; set; }
        public int RemovedMatchedSkinVpkCount { get; set; }
        public int MissingLiveVpkCount { get; set; }
        public int SkippedChangedLiveVpkCount { get; set; }
        public int RemovedManifestEntryCount { get; set; }
        public int ExpectedDlmmManagedModCount { get; set; }
        public int PreservedDlmmLiveVpkCount { get; set; }
        public int RemovedUnexpectedLiveVpkCount { get; set; }
        public bool RequiresDlmmApply { get; set; }
        public string ManifestPath { get; set; } = "";
        public RepairPreservationResult Preservation { get; set; } = new();
        public List<string> RemovedLiveVpks { get; set; } = [];
        public List<string> SkippedLiveVpks { get; set; } = [];
        public List<string> RemovedUnexpectedLiveVpks { get; set; } = [];
    }
}
