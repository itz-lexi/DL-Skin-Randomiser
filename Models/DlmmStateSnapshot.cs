namespace DL_Skin_Randomiser.Models
{
    public class DlmmStateSnapshot
    {
        public string Path { get; set; } = "";
        public string ActiveProfileId { get; set; } = "";
        public List<DlmmMod> Mods { get; set; } = [];
    }
}
