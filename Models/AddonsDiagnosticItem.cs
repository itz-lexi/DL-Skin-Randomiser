namespace DL_Skin_Randomiser.Models
{
    public class AddonsDiagnosticItem
    {
        public string Status { get; set; } = "";
        public string ModName { get; set; } = "";
        public string RemoteId { get; set; } = "";
        public string LiveSlot { get; set; } = "";
        public string StoredSlots { get; set; } = "";
        public string Evidence { get; set; } = "";
        public string Detail { get; set; } = "";
        public int SortRank { get; set; }
        public string StatusBrush { get; set; } = "#AEB7BA";
    }
}
