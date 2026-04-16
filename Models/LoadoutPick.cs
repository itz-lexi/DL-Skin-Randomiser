namespace DL_Skin_Randomiser.Models
{
    public class LoadoutPick
    {
        public string Hero { get; set; } = "";
        public string ModName { get; set; } = "";
        public string RemoteId { get; set; } = "";
        public string HeroDisplay => Services.HeroDisplayService.ToDisplayName(Hero);
    }
}
