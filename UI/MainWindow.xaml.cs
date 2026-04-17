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
using System.Diagnostics;
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
        private static readonly TimeSpan NoticeTickInterval = TimeSpan.FromMilliseconds(100);

        private readonly string _preferencesPath = UserPreferenceService.DefaultPreferencesPath;
        private string _statePath = "";
        private string _gamePath = "";
        private string _selectedProfileId = "";
        private List<DlmmMod> _mods = [];
        private List<LoadoutPick> _currentLoadout = [];
        private string _expandedSectionKey = "";
        private string _folderOptionsSignature = "";
        private string _characterOptionsSignature = "";
        private UserPreferences _preferences = new();
        private readonly System.Windows.Threading.DispatcherTimer _noticeTimer = new();
        private bool _isBindingGroups;
        private bool _isLoading;
        private DateTime _noticeExpiresAt;
        private TimeSpan _noticeDuration = TimeSpan.Zero;

        public List<CharacterOption> CharacterOptions { get; private set; } = [];
        public List<CharacterOption> FolderOptions { get; private set; } = [];
        public List<CharacterOption> ProfileOptions { get; private set; } = [];

        private enum NoticeKind
        {
            Info,
            Success,
            Warning,
            Error
        }

        public MainWindow()
        {
            InitializeComponent();
            _noticeTimer.Interval = NoticeTickInterval;
            _noticeTimer.Tick += NoticeTimer_Tick;
            Loaded += (_, _) => LoadMods();
        }

        private void LoadMods()
        {
            _isLoading = true;
            _preferences = UserPreferenceService.Load(_preferencesPath);
            _statePath = EnsureStatePath(_preferences);

            if (string.IsNullOrWhiteSpace(_statePath))
            {
                SetNotice("Setup needed", "Select DLMM state.json to load mods.", NoticeKind.Warning, showBanner: true);
                StatePathText.Text = "";
                _isLoading = false;
                return;
            }

            if (!File.Exists(_statePath))
            {
                SetNotice("State missing", $"DLMM state not found: {_statePath}", NoticeKind.Error, showBanner: true);
                StatePathText.Text = _statePath;
                _isLoading = false;
                return;
            }

            var snapshot = DlmmStateService.Load(_statePath, _preferences.SelectedProfileId);
            _gamePath = snapshot.GamePath;
            _selectedProfileId = snapshot.SelectedProfileId;
            ProfileOptions = snapshot.Profiles;
            _mods = snapshot.Mods
                .Where(mod => mod.IsInSelectedProfile)
                .OrderBy(mod => mod.Hero)
                .ThenBy(mod => mod.Name)
                .ToList();

            UserPreferenceService.Apply(_mods, _preferences, _selectedProfileId);
            var profilePreferences = GetCurrentProfilePreferences();
            _currentLoadout = profilePreferences.LastSessionLoadout;
            _expandedSectionKey = profilePreferences.ExpandedSections?.FirstOrDefault() ?? "";
            RefreshCharacterOptions();
            RefreshFolderOptions();
            RefreshProfileOptions();
            RefreshLoadoutSummary();
            ApplyNonRandomizerExclusions();
            BindGroups();

            SetNotice("Profile loaded", $"Loaded {_mods.Count} mods from {GetSelectedProfileName()}. In use: {_mods.Count(mod => mod.Enabled)}", NoticeKind.Success);
            StatePathText.Text = _statePath;
            _isLoading = false;
        }

        private void RandomiseButton_Click(object sender, RoutedEventArgs e)
        {
            RandomiseCurrentLoadout();
            SetNotice("Reroll ready", $"Rerolled {_currentLoadout.Count} hero picks. Apply or launch when you are happy with them.", NoticeKind.Success, showBanner: true);
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ValidateApplyInputs(requireGamePath: false);
                UserPreferenceService.Save(_preferencesPath, _mods, _statePath, _selectedProfileId);
                var result = ModApplyService.Apply(_statePath, _gamePath, _mods, _selectedProfileId);
                if (result.WrittenCount == 0)
                {
                    SetNotice("Nothing applied", $"No mods were written to {GetSelectedProfileName()} because this profile has no visible mod entries.", NoticeKind.Warning, showBanner: true);
                    return;
                }

                SetNotice("Apply complete", BuildApplyStatus(result), NoticeKind.Success, showBanner: true);
            }
            catch (Exception ex)
            {
                SetNotice("Apply failed", $"Apply failed for {GetSelectedProfileName()}: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
        }

        private void RandomisePlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ValidateApplyInputs(requireGamePath: true);
                if (_currentLoadout.Count == 0)
                    RandomiseCurrentLoadout();

                UserPreferenceService.Save(_preferencesPath, _mods, _statePath, _selectedProfileId);
                var result = ModApplyService.Apply(_statePath, _gamePath, _mods, _selectedProfileId);
                GameLaunchService.Launch(_gamePath);
                SetNotice("Deadlock launched", $"Applied {_currentLoadout.Count} current picks to {GetSelectedProfileName()} and launched Deadlock. Use Reroll before launch when you want a different set.", NoticeKind.Success, showBanner: true);
            }
            catch (Exception ex)
            {
                SetNotice("Launch failed", $"Randomise & Play failed: {ex.Message}", NoticeKind.Error, showBanner: true);
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

            UserPreferenceService.AddCustomFolder(_preferencesPath, _selectedProfileId, folderName);
            _preferences = UserPreferenceService.Load(_preferencesPath);
            NewFolderTextBox.Text = "";
            RefreshFolderOptions();
            BindGroups();
            AutoSavePreferences(showStatus: false);
            SetNotice("Folder added", $"Added folder {folderName.Trim()}.", NoticeKind.Success);
        }

        private void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folder = RemoveFolderBox.SelectedValue as string ?? "";
            if (string.IsNullOrWhiteSpace(folder))
            {
                SetNotice("Choose a folder", "Choose a folder to remove.", NoticeKind.Warning, showBanner: true);
                return;
            }

            foreach (var mod in _mods.Where(mod => string.Equals(NormalizeFolder(mod.Folder), folder, StringComparison.OrdinalIgnoreCase)))
            {
                mod.Folder = "";
                mod.IncludedInRandomizer = false;
            }

            UserPreferenceService.RemoveCustomFolder(_preferencesPath, _selectedProfileId, folder);
            _preferences = UserPreferenceService.Load(_preferencesPath);
            FolderFilterBox.Text = AllFoldersFilter;
            RemoveFolderBox.SelectedValue = null;
            RefreshFolderOptions();
            BindGroups();
            AutoSavePreferences(showStatus: false);
            SetNotice("Folder removed", $"Removed folder {GetFolderDisplayName(folder)}.", NoticeKind.Success, showBanner: true);
        }

        private void EditFolderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isLoading)
                return;

            var folder = EditFolderBox.SelectedValue as string ?? "";
            EditFolderNameTextBox.Text = string.IsNullOrWhiteSpace(folder)
                ? ""
                : GetFolderDisplayName(folder);
        }

        private void RenameFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var oldFolder = EditFolderBox.SelectedValue as string ?? "";
            var newFolderName = EditFolderNameTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(oldFolder))
            {
                SetNotice("Choose a folder", "Choose a folder to rename.", NoticeKind.Warning, showBanner: true);
                return;
            }

            if (string.IsNullOrWhiteSpace(newFolderName))
            {
                SetNotice("Name needed", "Type the new folder name first.", NoticeKind.Warning, showBanner: true);
                return;
            }

            var oldDisplayName = GetFolderDisplayName(oldFolder);
            UserPreferenceService.RenameCustomFolder(_preferencesPath, _selectedProfileId, oldFolder, newFolderName);

            foreach (var mod in _mods.Where(mod => string.Equals(NormalizeFolder(mod.Folder), oldFolder, StringComparison.OrdinalIgnoreCase)))
            {
                mod.Folder = newFolderName;
                mod.IncludedInRandomizer = false;
            }

            _preferences = UserPreferenceService.Load(_preferencesPath);
            RefreshFolderOptions();
            EditFolderBox.SelectedValue = HeroDisplayService.ToKey(newFolderName);
            FolderFilterBox.Text = string.Equals(NormalizeFolder(FolderFilterBox.Text), oldFolder, StringComparison.OrdinalIgnoreCase)
                ? HeroDisplayService.ToFolderDisplayName(newFolderName)
                : FolderFilterBox.Text;
            BindGroups();
            AutoSavePreferences(showStatus: false);
            SetNotice("Folder renamed", $"Renamed {oldDisplayName} to {newFolderName}.", NoticeKind.Success, showBanner: true);
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

            SetNotice(
                "DLMM state",
                File.Exists(suggestedPath)
                    ? $"Confirm DLMM state path: {suggestedPath}"
                    : "Select DLMM state.json to finish setup.",
                NoticeKind.Info,
                showBanner: true);

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
            var searchText = NormalizeSearch(ModSearchBox.Text);
            var inUseOnly = InUseFilterCheckBox.IsChecked == true;
            var inRandomizerOnly = InRandomizerFilterCheckBox.IsChecked == true;
            var groupedMods = _mods
                .Where(mod => CharacterMatchesFilter(mod, filter))
                .Where(mod => FolderMatchesFilter(mod, folderFilter))
                .Where(mod => !inUseOnly || mod.Enabled)
                .Where(mod => !inRandomizerOnly || mod.IncludedInRandomizer)
                .Where(mod => SearchMatchesFilter(mod, searchText))
                .GroupBy(GetSectionKey)
                .Select(group => new HeroModGroup
                {
                    Hero = group.Key,
                    IsFolder = group.Any(mod => !string.IsNullOrWhiteSpace(NormalizeFolder(mod.Folder))),
                    DisplayName = group.Any(mod => !string.IsNullOrWhiteSpace(NormalizeFolder(mod.Folder)))
                        ? GetFolderDisplayName(group.Key)
                        : "",
                    IsExpanded = ShouldExpandGroup(group.Key),
                    Mods = group.OrderByDescending(mod => mod.Enabled)
                        .ThenByDescending(mod => mod.IncludedInRandomizer)
                        .ThenBy(mod => mod.Name)
                        .ToList()
                })
                .OrderBy(GetSectionSortRank)
                .ThenBy(group => group.DisplayHero)
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
            UserPreferenceService.SaveLastSessionLoadout(_preferencesPath, _selectedProfileId, _currentLoadout);
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
            var profilePreferences = GetCurrentProfilePreferences();
            var folderKeys = profilePreferences.CustomFolders
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .GroupBy(HeroDisplayService.ToKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(folder => folder)
                .ToList();

            List<CharacterOption> nextFolderOptions =
            [
                new CharacterOption { Key = "", Name = "(None)" },
                ..folderKeys.Select(folder => new CharacterOption
                {
                    Key = HeroDisplayService.ToKey(folder),
                    Name = HeroDisplayService.ToFolderDisplayName(folder)
                })
            ];

            var signature = BuildOptionSignature(nextFolderOptions);
            if (string.Equals(signature, _folderOptionsSignature, StringComparison.Ordinal))
                return;

            _folderOptionsSignature = signature;
            FolderOptions = nextFolderOptions;

            var selectedFilter = FolderFilterBox.Text;
            FolderFilterBox.ItemsSource = new[] { AllFoldersFilter }
                .Concat(FolderOptions.Where(option => !string.IsNullOrWhiteSpace(option.Key)).Select(option => option.Name))
                .ToList();

            if (string.IsNullOrWhiteSpace(selectedFilter))
                FolderFilterBox.Text = AllFoldersFilter;
            else
                FolderFilterBox.Text = selectedFilter;

            var selectedRemoveFolder = RemoveFolderBox.SelectedValue as string;
            var selectedEditFolder = EditFolderBox.SelectedValue as string;
            RemoveFolderBox.ItemsSource = FolderOptions
                .Where(option => !string.IsNullOrWhiteSpace(option.Key))
                .ToList();
            RemoveFolderBox.SelectedValue = selectedRemoveFolder;

            EditFolderBox.ItemsSource = FolderOptions
                .Where(option => !string.IsNullOrWhiteSpace(option.Key))
                .ToList();
            EditFolderBox.SelectedValue = selectedEditFolder;
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

            var signature = BuildOptionSignature(CharacterOptions);
            if (string.Equals(signature, _characterOptionsSignature, StringComparison.Ordinal))
                return;

            _characterOptionsSignature = signature;

            var selectedFilter = CharacterFilterBox.Text;
            CharacterFilterBox.ItemsSource = new[] { AllCharactersFilter }
                .Concat(CharacterOptions.Select(option => option.Name))
                .ToList();

            if (string.IsNullOrWhiteSpace(selectedFilter))
                CharacterFilterBox.Text = AllCharactersFilter;
            else
                CharacterFilterBox.Text = selectedFilter;
        }

        private void RefreshProfileOptions()
        {
            ProfileBox.ItemsSource = ProfileOptions;
            ProfileBox.SelectedValue = _selectedProfileId;

            if (ProfileBox.SelectedValue is null && ProfileOptions.Count > 0)
            {
                ProfileBox.SelectedValue = ProfileOptions[0].Key;
                _selectedProfileId = ProfileOptions[0].Key;
            }
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

        private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            Dispatcher.BeginInvoke(BindGroups);
        }

        private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            if (ProfileBox.SelectedValue is not string selectedProfileId || string.IsNullOrWhiteSpace(selectedProfileId))
                return;

            if (string.Equals(selectedProfileId, _selectedProfileId, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedProfileId = selectedProfileId;
            UserPreferenceService.SaveSelectedProfile(_preferencesPath, _selectedProfileId);
            SetNotice("Loading profile", $"Loading {GetSelectedProfileName()}...", NoticeKind.Info);
            LoadMods();
        }

        private void FolderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                DlmmMod? changedMod = null;
                if (sender is ComboBox { DataContext: DlmmMod mod })
                {
                    changedMod = mod;
                    if (!string.IsNullOrWhiteSpace(mod.Folder))
                    {
                        mod.Hero = "unknown";
                        mod.IncludedInRandomizer = false;
                    }
                }

                ApplyNonRandomizerExclusions(changedMod);
                BindGroups();
                AutoSavePreferences(mod: changedMod);
            });
        }

        private void FolderSelector_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            ApplyNonRandomizerExclusions();
            BindGroups();
            AutoSavePreferences();
        }

        private void CharacterSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                DlmmMod? changedMod = null;
                if (sender is ComboBox { DataContext: DlmmMod mod })
                {
                    changedMod = mod;
                    var hero = NormalizeHero(mod.Hero);
                    if (!string.IsNullOrWhiteSpace(hero) && !string.Equals(hero, "unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        mod.Hero = hero;
                        mod.Folder = "";
                        mod.IncludedInRandomizer = true;
                    }
                    else
                    {
                        mod.Hero = "unknown";
                        mod.IncludedInRandomizer = false;
                    }
                }

                BindGroups();
                AutoSavePreferences(mod: changedMod);
            });
        }

        private void HeroSection_Expanded(object sender, RoutedEventArgs e)
        {
            if (_isBindingGroups || sender is not Expander { DataContext: HeroModGroup selectedGroup })
                return;

            _expandedSectionKey = selectedGroup.Hero;
            UserPreferenceService.SaveExpandedSections(_preferencesPath, _selectedProfileId, [_expandedSectionKey]);

            if (HeroGroupsList.ItemsSource is not IEnumerable<HeroModGroup> groups)
                return;

            foreach (var group in groups)
            {
                group.IsExpanded = string.Equals(group.Hero, selectedGroup.Hero, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void HeroSection_Collapsed(object sender, RoutedEventArgs e)
        {
            if (_isBindingGroups || sender is not Expander { DataContext: HeroModGroup selectedGroup })
                return;

            if (string.Equals(_expandedSectionKey, selectedGroup.Hero, StringComparison.OrdinalIgnoreCase))
            {
                _expandedSectionKey = "";
                UserPreferenceService.SaveExpandedSections(_preferencesPath, _selectedProfileId, []);
            }
        }

        private void ModPreference_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            if (sender is FrameworkElement { DataContext: DlmmMod mod })
            {
                ApplyNonRandomizerExclusions(mod);
                AutoSavePreferences(mod: mod);
                return;
            }

            ApplyNonRandomizerExclusions();
            AutoSavePreferences();
        }

        private void ApplyNonRandomizerExclusions(DlmmMod? changedMod = null)
        {
            if (changedMod is not null)
            {
                ApplyNonRandomizerExclusion(changedMod);
                return;
            }

            foreach (var mod in _mods)
            {
                ApplyNonRandomizerExclusion(mod);
            }
        }

        private static void ApplyNonRandomizerExclusion(DlmmMod mod)
        {
            if (!string.IsNullOrWhiteSpace(mod.Folder) || NormalizeHero(mod.Hero) == "unknown")
                mod.IncludedInRandomizer = false;
        }

        private void AutoSavePreferences(bool showStatus = true, DlmmMod? mod = null)
        {
            ApplyNonRandomizerExclusions(mod);
            if (mod is not null)
                UserPreferenceService.SaveMod(_preferencesPath, mod, _statePath, _selectedProfileId);
            else
                UserPreferenceService.Save(_preferencesPath, _mods, _statePath, _selectedProfileId);

            _preferences = UserPreferenceService.Load(_preferencesPath);

            if (showStatus)
                SetNotice("Saved", "Preferences saved.", NoticeKind.Success);
        }

        private void DismissNoticeButton_Click(object sender, RoutedEventArgs e)
        {
            HideNoticeBanner();
        }

        private void DiscordLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });

            e.Handled = true;
        }

        private void NoticeTimer_Tick(object? sender, EventArgs e)
        {
            if (_noticeDuration <= TimeSpan.Zero)
            {
                HideNoticeBanner();
                return;
            }

            var remaining = _noticeExpiresAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                HideNoticeBanner();
                return;
            }

            NoticeExpiryBar.Value = Math.Max(0, Math.Min(1, remaining.TotalMilliseconds / _noticeDuration.TotalMilliseconds));
        }

        private void SetNotice(string title, string message, NoticeKind kind = NoticeKind.Info, bool showBanner = false)
        {
            NoticeTitleText.Text = title;
            StatusText.Text = message;
            NoticeBannerText.Text = message;

            var (dotColor, bannerBackground, bannerBorder) = kind switch
            {
                NoticeKind.Success => ("#91D18B", "#203125", "#5EA86A"),
                NoticeKind.Warning => ("#D5B56E", "#332C1C", "#8B7334"),
                NoticeKind.Error => ("#F07C7C", "#341F20", "#A64949"),
                _ => ("#8FB9F2", "#1D2835", "#496D9C")
            };

            TrayStatusDot.Fill = BrushFrom(dotColor);
            NoticeBanner.Background = BrushFrom(bannerBackground);
            NoticeBanner.BorderBrush = BrushFrom(bannerBorder);
            NoticeExpiryBar.Foreground = BrushFrom(bannerBorder);

            if (showBanner)
                ShowNoticeBanner(kind);
            else
                HideNoticeBanner();
        }

        private void ShowNoticeBanner(NoticeKind kind)
        {
            _noticeDuration = kind switch
            {
                NoticeKind.Error => TimeSpan.FromSeconds(12),
                NoticeKind.Warning => TimeSpan.FromSeconds(9),
                _ => TimeSpan.FromSeconds(6)
            };

            _noticeExpiresAt = DateTime.UtcNow.Add(_noticeDuration);
            NoticeExpiryBar.Value = 1;
            NoticeBanner.Visibility = Visibility.Visible;
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        private void HideNoticeBanner()
        {
            _noticeTimer.Stop();
            NoticeExpiryBar.Value = 0;
            NoticeBanner.Visibility = Visibility.Collapsed;
        }

        private static SolidColorBrush BrushFrom(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        }

        private string GetSelectedProfileName()
        {
            return ProfileOptions
                .FirstOrDefault(profile => string.Equals(profile.Key, _selectedProfileId, StringComparison.OrdinalIgnoreCase))
                ?.Name
                ?? "selected profile";
        }

        private void ValidateApplyInputs(bool requireGamePath)
        {
            if (string.IsNullOrWhiteSpace(_selectedProfileId))
                throw new InvalidOperationException("No DLMM profile is selected.");

            if (string.IsNullOrWhiteSpace(_statePath))
                throw new InvalidOperationException("No DLMM state file is selected.");

            if (!File.Exists(_statePath))
                throw new InvalidOperationException($"DLMM state file was not found: {_statePath}");

            if (_mods.Count == 0)
                throw new InvalidOperationException("No mods are loaded for the selected profile.");

            if (!requireGamePath)
                return;

            if (string.IsNullOrWhiteSpace(_gamePath) || !Directory.Exists(_gamePath))
                throw new InvalidOperationException("Deadlock game path was not found in DLMM state, so the app cannot launch or stage game files.");
        }

        private string BuildApplyStatus(ApplyResult result)
        {
            var status = $"Game files updated for {GetSelectedProfileName()}. In use: {result.EnabledCount}. Staged +{result.StagedEnabledCount}/-{result.StagedDisabledCount}.";
            if (result.ForcedDisabledCount > 0)
                status += $" Disabled {result.ForcedDisabledCount} duplicate hero picks.";
            if (result.StagingSkippedCount > 0)
                status += $" {result.StagingSkippedCount} mods had no game files to stage.";
            if (!string.IsNullOrWhiteSpace(result.AddonsBackupPath))
                status += " Backup created.";

            status += " DLMM state was written too.";

            return IsDlmmRunning()
                ? $"{status} DLMM is open, so its window may still show the old selection until restart."
                : status;
        }

        private static bool IsDlmmRunning()
        {
            return Process.GetProcesses()
                .Any(process =>
                    process.ProcessName.Contains("deadlock-mod-manager", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName.Contains("Deadlock Mod Manager", StringComparison.OrdinalIgnoreCase));
        }

        private ProfilePreferences GetCurrentProfilePreferences()
        {
            return UserPreferenceService.GetProfilePreferences(_preferences, _selectedProfileId, useLegacyFallback: true);
        }

        private bool ShouldExpandGroup(string groupKey)
        {
            if (string.IsNullOrWhiteSpace(_expandedSectionKey))
                return false;

            return string.Equals(groupKey, _expandedSectionKey, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetSectionSortRank(HeroModGroup group)
        {
            if (string.Equals(group.Hero, "unknown", StringComparison.OrdinalIgnoreCase))
                return 2;

            return group.IsFolder ? 1 : 0;
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

        private string GetFolderDisplayName(string folderKey)
        {
            return FolderOptions
                .FirstOrDefault(option => string.Equals(option.Key, HeroDisplayService.ToKey(folderKey), StringComparison.OrdinalIgnoreCase))
                ?.Name
                ?? HeroDisplayService.ToFolderDisplayName(folderKey);
        }

        private static string BuildOptionSignature(IEnumerable<CharacterOption> options)
        {
            return string.Join(
                "|",
                options.Select(option => $"{option.Key}\u001f{option.Name}"));
        }
    }
}
