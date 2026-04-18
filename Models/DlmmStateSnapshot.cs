namespace DL_Skin_Randomiser.Models
{
    public class DlmmStateSnapshot
    {
        public string Path { get; set; } = "";
        public string ActiveProfileId { get; set; } = "";
        public string SelectedProfileId { get; set; } = "";
        public string GamePath { get; set; } = "";
        public List<CharacterOption> Profiles { get; set; } = [];
        public List<DlmmMod> Mods { get; set; } = [];
        public List<DlmmMod> AllMods { get; set; } = [];
    }
}
