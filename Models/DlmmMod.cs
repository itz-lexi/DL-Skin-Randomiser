using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DL_Skin_Randomiser.Models
{
    public class DlmmMod : INotifyPropertyChanged
    {
        private string _hero = "";
        private string _folder = "";
        private bool _includedInRandomizer = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = "";
        public string RemoteId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Category { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public List<string> InstalledVpks { get; set; } = [];
        public bool IsInSelectedProfile { get; set; }
        public string Hero
        {
            get => _hero;
            set
            {
                if (_hero == value)
                    return;

                _hero = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeroDisplay));
                OnPropertyChanged(nameof(ShowFolderSelector));
                OnPropertyChanged(nameof(AssignmentKey));
            }
        }
        public string Folder
        {
            get => _folder;
            set
            {
                if (_folder == value)
                    return;

                _folder = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FolderDisplay));
                OnPropertyChanged(nameof(ShowCharacterSelector));
                OnPropertyChanged(nameof(ShowFolderSelector));
                OnPropertyChanged(nameof(AssignmentKey));
            }
        }
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
        public bool ShowCharacterSelector => string.IsNullOrWhiteSpace(Folder);
        public bool ShowFolderSelector =>
            !string.IsNullOrWhiteSpace(Folder)
            || string.IsNullOrWhiteSpace(Hero)
            || string.Equals(Hero, "unknown", StringComparison.OrdinalIgnoreCase);
        public string AssignmentKey
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Folder))
                    return $"folder:{Services.HeroDisplayService.ToKey(Folder)}";

                return $"hero:{Services.HeroDisplayService.ToKey(Hero)}";
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                const string heroPrefix = "hero:";
                const string folderPrefix = "folder:";

                if (value.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    Folder = Services.HeroDisplayService.ToKey(value[folderPrefix.Length..]);
                    Hero = "unknown";
                    IncludedInRandomizer = false;
                    return;
                }

                if (value.StartsWith(heroPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    Hero = Services.HeroDisplayService.ToKey(value[heroPrefix.Length..]);
                    Folder = "";
                    IncludedInRandomizer = !string.Equals(Hero, "unknown", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        public bool IncludedInRandomizer
        {
            get => _includedInRandomizer;
            set
            {
                if (_includedInRandomizer == value)
                    return;

                _includedInRandomizer = value;
                OnPropertyChanged();
            }
        }
        public bool Enabled { get; set; }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
