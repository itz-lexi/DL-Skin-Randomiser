namespace DL_Skin_Randomiser.Models
{
    public class ProfilePreferences
    {
        public List<string> CustomFolders { get; set; } = [];
        public List<LoadoutPick> LastSessionLoadout { get; set; } = [];
        public List<LoadoutPreset> RecentSessionPresets { get; set; } = [];
        public List<LoadoutPreset> SavedPresets { get; set; } = [];
        public List<string> ExpandedSections { get; set; } = [];
        public Dictionary<string, ModPreference> Mods { get; set; } = [];
    }
}
