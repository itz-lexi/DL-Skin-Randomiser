using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using DL_Skin_Randomiser.Models;
using DL_Skin_Randomiser.Services;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace DL_Skin_Randomiser
{
    public partial class MainWindow : Window
    {
        private const string AllCharactersFilter = "All characters";
        private const string AllFoldersFilter = "All folders";

        private readonly string _preferencesPath = UserPreferenceService.DefaultPreferencesPath;
        private string _statePath = "";
        private List<DlmmMod> _mods = [];
        private UserPreferences _preferences = new();
        private bool _isBindingGroups;
        private bool _isLoading;

        public List<CharacterOption> CharacterOptions { get; private set; } = [];
        public List<string> FolderOptions { get; private set; } = [];

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => LoadMods();
        }

        private void LoadMods()
        {
            _isLoading = true;
            _preferences = UserPreferenceService.Load(_preferencesPath);
            _statePath = EnsureStatePath(_preferences);

            if (string.IsNullOrWhiteSpace(_statePath))
            {
                StatusText.Text = "Select DLMM state.json to load mods.";
                _isLoading = false;
                return;
            }

            if (!File.Exists(_statePath))
            {
                StatusText.Text = $"DLMM state not found: {_statePath}";
                _isLoading = false;
                return;
            }

            _mods = ModService.LoadMods(_statePath)
                .OrderBy(mod => mod.Hero)
                .ThenBy(mod => mod.Name)
                .ToList();

            UserPreferenceService.Apply(_mods, _preferences);
            RefreshCharacterOptions();
            RefreshFolderOptions();
            BindGroups();

            foreach (var mod in _mods)
            {
                Console.WriteLine($"{mod.Name} - {mod.Enabled}");
            }

            StatusText.Text = $"Loaded {_mods.Count} mods. Enabled: {_mods.Count(mod => mod.Enabled)}";
            _isLoading = false;
        }

        private void RandomiseButton_Click(object sender, RoutedEventArgs e)
        {
            _mods = ModSelectionService.RandomlySelectOnePerHero(_mods)
                .OrderBy(mod => mod.Hero)
                .ThenBy(mod => mod.Name)
                .ToList();

            BindGroups();
            AutoSavePreferences(showStatus: false);
            StatusText.Text = $"Randomised {_mods.Count(mod => mod.IncludedInRandomizer && mod.Hero != "unknown")} included hero mods.";
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            UserPreferenceService.Save(_preferencesPath, _mods, _statePath);
            ModApplyService.Apply(_statePath, _mods);
            StatusText.Text = "Applied selection to DLMM state and saved preferences. Backup created next to state.json.";
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            LoadMods();
        }

        private void BrowseDlmmButton_Click(object sender, RoutedEventArgs e)
        {
            var preferences = UserPreferenceService.Load(_preferencesPath);
            var selectedPath = SelectStatePath(preferences.StatePath, forcePrompt: true);
            if (string.IsNullOrWhiteSpace(selectedPath))
                return;

            _statePath = selectedPath;
            UserPreferenceService.SaveStatePath(_preferencesPath, _statePath);
            LoadMods();
        }

        private void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folderName = NewFolderTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folderName))
                return;

            UserPreferenceService.AddCustomFolder(_preferencesPath, folderName);
            _preferences = UserPreferenceService.Load(_preferencesPath);
            NewFolderTextBox.Text = "";
            RefreshFolderOptions();
            AutoSavePreferences(showStatus: false);
            StatusText.Text = $"Added folder {HeroDisplayService.ToDisplayName(folderName)}.";
        }

        private string EnsureStatePath(UserPreferences preferences)
        {
            if (!string.IsNullOrWhiteSpace(preferences.StatePath) && File.Exists(preferences.StatePath))
                return preferences.StatePath;

            var selectedPath = SelectStatePath(preferences.StatePath, forcePrompt: string.IsNullOrWhiteSpace(preferences.StatePath));
            if (string.IsNullOrWhiteSpace(selectedPath))
                return "";

            UserPreferenceService.SaveStatePath(_preferencesPath, selectedPath);
            return selectedPath;
        }

        private string SelectStatePath(string savedPath, bool forcePrompt)
        {
            var suggestedPath = FindSuggestedStatePath(savedPath);

            if (!forcePrompt && File.Exists(suggestedPath))
                return suggestedPath;

            var dialog = new OpenFileDialog
            {
                Title = "Select DLMM state.json",
                Filter = "DLMM state.json|state.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                FileName = "state.json"
            };

            if (File.Exists(suggestedPath))
            {
                dialog.InitialDirectory = IOPath.GetDirectoryName(suggestedPath);
                dialog.FileName = IOPath.GetFileName(suggestedPath);
            }
            else
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrWhiteSpace(appData))
                    dialog.InitialDirectory = appData;
            }

            var result = dialog.ShowDialog();
            if (result == true)
                return dialog.FileName;

            return File.Exists(suggestedPath) ? suggestedPath : "";
        }

        private static string FindSuggestedStatePath(string savedPath)
        {
            var candidates = new[]
            {
                savedPath,
                DlmmStateService.DefaultStatePath,
                IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "dev.stormix.deadlock-mod-manager",
                    "state.json"),
                IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Deadlock Mod Manager",
                    "state.json")
            };

            return candidates.FirstOrDefault(candidate => File.Exists(candidate)) ?? DlmmStateService.DefaultStatePath;
        }

        private void BindGroups()
        {
            _isBindingGroups = true;

            var filter = NormalizeFilter(CharacterFilterBox.Text);
            var folderFilter = NormalizeFolder(FolderFilterBox.Text);
            var groupedMods = _mods
                .Where(mod => CharacterMatchesFilter(mod, filter))
                .Where(mod => FolderMatchesFilter(mod, folderFilter))
                .GroupBy(mod => NormalizeHero(mod.Hero))
                .Select(group => new HeroModGroup
                {
                    Hero = group.Key,
                    Mods = group.OrderByDescending(mod => mod.Enabled)
                        .ThenByDescending(mod => mod.IncludedInRandomizer)
                        .ThenBy(mod => mod.Name)
                        .ToList()
                })
                .OrderBy(group => group.Hero == "unknown")
                .ThenBy(group => group.Hero)
                .ToList();

            HeroGroupsList.ItemsSource = groupedMods;
            _isBindingGroups = false;
        }

        private void RefreshFolderOptions()
        {
            FolderOptions = _preferences.CustomFolders
                .Concat(_mods
                    .Where(mod => !string.IsNullOrWhiteSpace(mod.Folder))
                    .Select(mod => HeroDisplayService.ToDisplayName(mod.Folder)))
                .Where(folder => !string.IsNullOrWhiteSpace(folder) && NormalizeFolder(folder) != "none")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(folder => folder)
                .ToList();

            var selectedFilter = FolderFilterBox.Text;
            FolderFilterBox.ItemsSource = new[] { AllFoldersFilter }.Concat(FolderOptions).ToList();

            if (string.IsNullOrWhiteSpace(selectedFilter))
                FolderFilterBox.Text = AllFoldersFilter;
            else
                FolderFilterBox.Text = selectedFilter;
        }

        private void RefreshCharacterOptions()
        {
            CharacterOptions = HeroDetector.KnownHeroes
                .Concat(_mods.Select(mod => NormalizeHero(mod.Hero)))
                .Where(hero => !string.IsNullOrWhiteSpace(hero))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(hero => hero == "unknown")
                .ThenBy(hero => hero)
                .Select(hero => new CharacterOption
                {
                    Key = hero,
                    Name = HeroDisplayService.ToDisplayName(hero)
                })
                .ToList();

            var selectedFilter = CharacterFilterBox.Text;
            CharacterFilterBox.ItemsSource = new[] { AllCharactersFilter }
                .Concat(CharacterOptions.Select(option => option.Name))
                .ToList();

            if (string.IsNullOrWhiteSpace(selectedFilter))
                CharacterFilterBox.Text = AllCharactersFilter;
            else
                CharacterFilterBox.Text = selectedFilter;
        }

        private void CharacterFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups)
                return;

            if (CharacterFilterBox.SelectedItem is string selectedCharacter)
                CharacterFilterBox.Text = selectedCharacter;

            Dispatcher.BeginInvoke(BindGroups);
        }

        private void CharacterFilterBox_DropDownClosed(object sender, EventArgs e)
        {
            if (!IsLoaded || _isBindingGroups)
                return;

            BindGroups();
        }

        private void CharacterFilterBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups)
                return;

            BindGroups();
        }

        private void FolderFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups)
                return;

            if (FolderFilterBox.SelectedItem is string selectedFolder)
                FolderFilterBox.Text = selectedFolder;

            Dispatcher.BeginInvoke(BindGroups);
        }

        private void FolderFilterBox_DropDownClosed(object sender, EventArgs e)
        {
            if (!IsLoaded || _isBindingGroups)
                return;

            BindGroups();
        }

        private void FolderFilterBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups)
                return;

            BindGroups();
        }

        private void FolderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                ApplyFolderExclusions();
                RefreshFolderOptions();
                AutoSavePreferences();
            });
        }

        private void FolderSelector_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            ApplyFolderExclusions();
            RefreshFolderOptions();
            AutoSavePreferences();
        }

        private void CharacterSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            AutoSavePreferences();
        }

        private void ModPreference_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            ApplyFolderExclusions();
            AutoSavePreferences();
        }

        private void ApplyFolderExclusions()
        {
            foreach (var mod in _mods.Where(mod => !string.IsNullOrWhiteSpace(mod.Folder)))
            {
                mod.IncludedInRandomizer = false;
            }
        }

        private void AutoSavePreferences(bool showStatus = true)
        {
            UserPreferenceService.Save(_preferencesPath, _mods, _statePath);
            _preferences = UserPreferenceService.Load(_preferencesPath);

            if (showStatus)
                StatusText.Text = "Preferences saved.";
        }

        private void MainScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsInsideComboBox(e.OriginalSource as DependencyObject))
                return;

            MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - e.Delta * 2.2);
            e.Handled = true;
        }

        private static bool IsInsideComboBox(DependencyObject? source)
        {
            while (source is not null)
            {
                if (source is ComboBox or ComboBoxItem)
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private static bool CharacterMatchesFilter(DlmmMod mod, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            return NormalizeHero(mod.Hero).Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        private static bool FolderMatchesFilter(DlmmMod mod, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            return NormalizeFolder(mod.Folder).Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFilter(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return "";

            var normalized = filter.Trim().ToLowerInvariant();
            return normalized == AllCharactersFilter.ToLowerInvariant()
                ? ""
                : normalized;
        }

        private static string NormalizeHero(string hero)
        {
            return string.IsNullOrWhiteSpace(hero)
                ? "unknown"
                : hero.Trim().ToLowerInvariant();
        }

        private static string NormalizeFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return "";

            var normalized = folder.Trim().ToLowerInvariant();
            return normalized == AllFoldersFilter.ToLowerInvariant()
                ? ""
                : normalized;
        }
    }
}
