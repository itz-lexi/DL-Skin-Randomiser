namespace DL_Skin_Randomiser.Models
{
    public class DlmmMod
    {
        public string Id { get; set; } = "";
        public string RemoteId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Category { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public string Hero { get; set; } = "";
        public string Folder { get; set; } = "";
        public string FolderDisplay
        {
            get => string.IsNullOrWhiteSpace(Folder) ? "" : Services.HeroDisplayService.ToDisplayName(Folder);
            set
            {
                Folder = Services.HeroDisplayService.ToKey(value);
                if (Folder == "unknown")
                    Folder = "";

                if (!string.IsNullOrWhiteSpace(Folder))
                    IncludedInRandomizer = false;
            }
        }
        public string HeroDisplay
        {
            get => Services.HeroDisplayService.ToDisplayName(Hero);
            set => Hero = Services.HeroDisplayService.ToKey(value);
        }
        public bool IncludedInRandomizer { get; set; } = true;
        public bool Enabled { get; set; }
    }
}
