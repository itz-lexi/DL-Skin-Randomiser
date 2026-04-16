namespace DL_Skin_Randomiser.Models
{
    public class UserPreferences
    {
        public string StatePath { get; set; } = "";
        public List<string> CustomFolders { get; set; } = [];
        public Dictionary<string, ModPreference> Mods { get; set; } = [];
    }
}
