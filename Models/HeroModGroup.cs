namespace DL_Skin_Randomiser.Models
{
    public class HeroModGroup
    {
        public string Hero { get; set; } = "";
        public string DisplayHero => Services.HeroDisplayService.ToDisplayName(Hero);
        public string BackgroundHero => DisplayHero.ToUpperInvariant();
        public List<DlmmMod> Mods { get; set; } = [];
        public int IncludedCount => Mods.Count(mod => mod.IncludedInRandomizer);
        public int EnabledCount => Mods.Count(mod => mod.Enabled);
    }
}
