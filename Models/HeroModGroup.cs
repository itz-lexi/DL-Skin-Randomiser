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
