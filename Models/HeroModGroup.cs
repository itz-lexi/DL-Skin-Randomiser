namespace DL_Skin_Randomiser.Models
{
    public class HeroModGroup : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isExpanded;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Hero { get; set; } = "";
        public bool IsFolder { get; set; }
        public string DisplayName { get; set; } = "";
        public string DisplayHero => string.IsNullOrWhiteSpace(DisplayName)
            ? Services.HeroDisplayService.ToDisplayName(Hero)
            : DisplayName;
        public string BackgroundHero => DisplayHero.ToUpperInvariant();
        public List<DlmmMod> Mods { get; set; } = [];
        public int IncludedCount => Mods.Count(mod => mod.IncludedInRandomizer);
        public int EnabledCount => Mods.Count(mod => mod.Enabled);
        public bool IsUnknown => string.Equals(Hero, "unknown", StringComparison.OrdinalIgnoreCase) && !IsFolder;
        public string SectionKindText => IsFolder
            ? "Folder"
            : IsUnknown
                ? "Needs sorting"
                : "Character";
        public string ModCountText => FormatCount(Mods.Count, "mod");
        public string EnabledCountText => $"In use: {EnabledCount}";
        public string RandomizerCountText => IsFolder
            ? "Managed in DLMM"
            : IsUnknown
                ? "Not randomised"
                : $"Randomiser: {IncludedCount}";
        public string IncludedCountText => RandomizerCountText;
        public string SummaryText => $"{ModCountText} • {EnabledCountText} • {IncludedCountText}";

        private static string FormatCount(int count, string label)
        {
            if (label == "mod")
                return count == 1 ? "1 mod" : $"{count} mods";

            return $"{count} {label}";
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                    return;

                _isExpanded = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }
}
