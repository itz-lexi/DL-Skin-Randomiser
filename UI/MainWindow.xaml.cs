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
        private const string AllStatusesFilter = "All statuses";

        private readonly string _preferencesPath = UserPreferenceService.DefaultPreferencesPath;
        private string _statePath = "";
        private string _gamePath = "";
        private List<DlmmMod> _mods = [];
        private List<LoadoutPick> _currentLoadout = [];
        private UserPreferences _preferences = new();
        private bool _isBindingGroups;
        private bool _isLoading;

        public List<CharacterOption> CharacterOptions { get; private set; } = [];
        public List<CharacterOption> FolderOptions { get; private set; } = [];

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
                StatePathText.Text = "";
                _isLoading = false;
                return;
            }

            if (!File.Exists(_statePath))
            {
                StatusText.Text = $"DLMM state not found: {_statePath}";
                StatePathText.Text = _statePath;
                _isLoading = false;
                return;
            }

            var snapshot = DlmmStateService.Load(_statePath);
            _gamePath = snapshot.GamePath;
            _mods = snapshot.Mods
                .OrderBy(mod => mod.Hero)
                .ThenBy(mod => mod.Name)
                .ToList();

            UserPreferenceService.Apply(_mods, _preferences);
            _currentLoadout = _preferences.LastSessionLoadout;
            RefreshCharacterOptions();
            RefreshFolderOptions();
            RefreshStatusOptions();
            RefreshLoadoutSummary();
            BindGroups();

            foreach (var mod in _mods)
            {
                Console.WriteLine($"{mod.Name} - {mod.Enabled}");
            }

            StatusText.Text = $"Loaded {_mods.Count} mods. Enabled: {_mods.Count(mod => mod.Enabled)}";
            StatePathText.Text = _statePath;
            _isLoading = false;
        }

        private void RandomiseButton_Click(object sender, RoutedEventArgs e)
        {
            RandomiseCurrentLoadout();
            StatusText.Text = $"Randomised {_currentLoadout.Count} hero picks.";
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            UserPreferenceService.Save(_preferencesPath, _mods, _statePath);
            ModApplyService.Apply(_statePath, _mods);
            StatusText.Text = "Applied selection to DLMM state and saved preferences. Backup created next to state.json.";
        }

        private void RandomisePlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RandomiseCurrentLoadout();
                UserPreferenceService.Save(_preferencesPath, _mods, _statePath);
                ModApplyService.Apply(_statePath, _mods);
                GameLaunchService.Launch(_gamePath);
                StatusText.Text = $"Randomised {_currentLoadout.Count} picks, applied, and launched Deadlock.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Randomise & Play failed: {ex.Message}";
            }
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

        private void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folder = NormalizeFolder(FolderFilterBox.Text);
            if (string.IsNullOrWhiteSpace(folder))
            {
                StatusText.Text = "Choose a folder to remove.";
                return;
            }

            foreach (var mod in _mods.Where(mod => string.Equals(NormalizeFolder(mod.Folder), folder, StringComparison.OrdinalIgnoreCase)))
            {
                mod.Folder = "";
                mod.IncludedInRandomizer = false;
            }

            UserPreferenceService.RemoveCustomFolder(_preferencesPath, folder);
            _preferences = UserPreferenceService.Load(_preferencesPath);
            FolderFilterBox.Text = AllFoldersFilter;
            RefreshFolderOptions();
            RefreshCharacterOptions();
            BindGroups();
            AutoSavePreferences(showStatus: false);
            StatusText.Text = $"Removed folder {HeroDisplayService.ToDisplayName(folder)}.";
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

            StatusText.Text = File.Exists(suggestedPath)
                ? $"Confirm DLMM state path: {suggestedPath}"
                : "Select DLMM state.json to finish setup.";

            var dialog = new OpenFileDialog
            {
                Title = forcePrompt ? "Confirm DLMM state.json" : "Select DLMM state.json",
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
            var statusFilter = NormalizeStatus(StatusFilterBox.Text);
            var searchText = NormalizeSearch(ModSearchBox.Text);
            var groupedMods = _mods
                .Where(mod => CharacterMatchesFilter(mod, filter))
                .Where(mod => FolderMatchesFilter(mod, folderFilter))
                .Where(mod => StatusMatchesFilter(mod, statusFilter))
                .Where(mod => SearchMatchesFilter(mod, searchText))
                .GroupBy(GetSectionKey)
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
            Dispatcher.BeginInvoke(() =>
            {
                _isBindingGroups = false;
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void RandomiseCurrentLoadout()
        {
            _mods = ModSelectionService.RandomlySelectOnePerHero(_mods)
                .OrderBy(mod => mod.Hero)
                .ThenBy(mod => mod.Name)
                .ToList();

            _currentLoadout = BuildCurrentLoadout();
            UserPreferenceService.SaveLastSessionLoadout(_preferencesPath, _currentLoadout);
            _preferences = UserPreferenceService.Load(_preferencesPath);
            RefreshLoadoutSummary();
            BindGroups();
            AutoSavePreferences(showStatus: false);
        }

        private List<LoadoutPick> BuildCurrentLoadout()
        {
            return _mods
                .Where(mod => mod.Enabled
                    && mod.IncludedInRandomizer
                    && string.IsNullOrWhiteSpace(mod.Folder)
                    && NormalizeHero(mod.Hero) != "unknown")
                .Select(mod => new LoadoutPick
                {
                    Hero = NormalizeHero(mod.Hero),
                    ModName = mod.Name,
                    RemoteId = mod.RemoteId
                })
                .OrderBy(pick => pick.Hero)
                .ThenBy(pick => pick.ModName)
                .ToList();
        }

        private void RefreshLoadoutSummary()
        {
            LoadoutSummaryList.ItemsSource = _currentLoadout;
        }

        private void RefreshFolderOptions()
        {
            var folderKeys = _preferences.CustomFolders
                .Concat(_mods
                    .Where(mod => !string.IsNullOrWhiteSpace(mod.Folder))
                    .Select(mod => NormalizeFolder(mod.Folder)))
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(folder => HeroDisplayService.ToDisplayName(folder))
                .ToList();

            FolderOptions =
            [
                new CharacterOption { Key = "", Name = "(None)" },
                ..folderKeys.Select(folder => new CharacterOption
                {
                    Key = folder,
                    Name = HeroDisplayService.ToDisplayName(folder)
                })
            ];

            var selectedFilter = FolderFilterBox.Text;
            FolderFilterBox.ItemsSource = new[] { AllFoldersFilter }
                .Concat(FolderOptions.Where(option => !string.IsNullOrWhiteSpace(option.Key)).Select(option => option.Name))
                .ToList();

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

        private void RefreshStatusOptions()
        {
            var selectedFilter = StatusFilterBox.Text;
            var statuses = _mods
                .Select(mod => NormalizeStatus(mod.Status))
                .Where(status => !string.IsNullOrWhiteSpace(status))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(status => HeroDisplayService.ToDisplayName(status))
                .Select(HeroDisplayService.ToDisplayName)
                .ToList();

            StatusFilterBox.ItemsSource = new[] { AllStatusesFilter }.Concat(statuses).ToList();

            if (string.IsNullOrWhiteSpace(selectedFilter))
                StatusFilterBox.Text = AllStatusesFilter;
            else
                StatusFilterBox.Text = selectedFilter;
        }

        private void CharacterFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            if (CharacterFilterBox.SelectedItem is string selectedCharacter)
                CharacterFilterBox.Text = selectedCharacter;

            Dispatcher.BeginInvoke(BindGroups);
        }

        private void CharacterFilterBox_DropDownClosed(object sender, EventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            BindGroups();
        }

        private void CharacterFilterBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            BindGroups();
        }

        private void FolderFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            if (FolderFilterBox.SelectedItem is string selectedFolder)
                FolderFilterBox.Text = selectedFolder;

            Dispatcher.BeginInvoke(BindGroups);
        }

        private void FolderFilterBox_DropDownClosed(object sender, EventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            BindGroups();
        }

        private void FolderFilterBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            BindGroups();
        }

        private void ModSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            BindGroups();
        }

        private void StatusFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            if (StatusFilterBox.SelectedItem is string selectedStatus)
                StatusFilterBox.Text = selectedStatus;

            Dispatcher.BeginInvoke(BindGroups);
        }

        private void FolderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (sender is ComboBox { DataContext: DlmmMod mod })
                {
                    if (!string.IsNullOrWhiteSpace(mod.Folder))
                    {
                        mod.Hero = "unknown";
                        mod.IncludedInRandomizer = false;
                    }
                }

                ApplyFolderExclusions();
                RefreshFolderOptions();
                RefreshCharacterOptions();
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

            Dispatcher.BeginInvoke(() =>
            {
                if (sender is ComboBox { DataContext: DlmmMod mod })
                {
                    if (!string.IsNullOrWhiteSpace(mod.Hero) && !string.Equals(mod.Hero, "unknown", StringComparison.OrdinalIgnoreCase))
                        mod.Folder = "";
                }

                RefreshFolderOptions();
                AutoSavePreferences();
            });
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

        private static bool StatusMatchesFilter(DlmmMod mod, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            return NormalizeStatus(mod.Status).Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SearchMatchesFilter(DlmmMod mod, string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return true;

            var searchableValues = new[]
            {
                mod.Name,
                mod.Category,
                mod.Status,
                HeroDisplayService.ToDisplayName(mod.Hero),
                HeroDisplayService.ToDisplayName(mod.Folder),
                mod.RemoteId
            };

            return searchableValues.Any(value => value.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetSectionKey(DlmmMod mod)
        {
            var folder = NormalizeFolder(mod.Folder);
            if (!string.IsNullOrWhiteSpace(folder))
                return folder;

            return NormalizeHero(mod.Hero);
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

        private static string NormalizeSearch(string searchText)
        {
            return searchText.Trim();
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

        private static string NormalizeStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return "";

            var normalized = status.Trim().ToLowerInvariant();
            return normalized == AllStatusesFilter.ToLowerInvariant()
                ? ""
                : normalized;
        }
    }
}
