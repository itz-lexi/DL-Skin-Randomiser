namespace DL_Skin_Randomiser.Models
{
    public class ModPreference
    {
        public string RemoteId { get; set; } = "";
        public string Hero { get; set; } = "";
        public string Folder { get; set; } = "";
        public bool IncludedInRandomizer { get; set; } = true;
    }
}
