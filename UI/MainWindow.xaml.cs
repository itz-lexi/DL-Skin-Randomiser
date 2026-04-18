using System.Text;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Diagnostics;
using System.Reflection;
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
        private static readonly TimeSpan NoticeTickInterval = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan NoticeAnimationDuration = TimeSpan.FromMilliseconds(260);
        private static readonly TimeSpan NoticeDisplayDuration = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan GameLaunchSettleDelay = TimeSpan.FromSeconds(2);

        private readonly string _preferencesPath = UserPreferenceService.DefaultPreferencesPath;
        private readonly string _appVersion = GetAppVersion();
#if DEBUG
        private const bool IsDevBuild = true;
#else
        private const bool IsDevBuild = false;
#endif
        private string _statePath = "";
        private string _gamePath = "";
        private string _selectedProfileId = "";
        private List<DlmmMod> _mods = [];
        private List<DlmmMod> _allMods = [];
        private List<LoadoutPick> _currentLoadout = [];
        private string _expandedSectionKey = "";
        private string _folderOptionsSignature = "";
        private string _characterOptionsSignature = "";
        private string _groupOptionsSignature = "";
        private string _savedPresetOptionsSignature = "";
        private string _recentPresetOptionsSignature = "";
        private UserPreferences _preferences = new();
        private readonly System.Windows.Threading.DispatcherTimer _noticeTimer = new();
        private readonly System.Windows.Threading.DispatcherTimer _gameMonitorTimer = new();
        private readonly DiscordRichPresenceService _discordPresence = DiscordRichPresenceService.FromAppConfiguration();
        private UpdateCheckResult? _latestUpdate;
        private AddonsReconciliationResult _addonsState = new();
        private bool _isBindingGroups;
        private bool _bindGroupsAgain;
        private bool _isLoading;
        private bool _isWatchingLaunchedGame;
        private bool _discordShowsInGame;
        private bool _isShuttingDown;
        private DateTime _lastPhysicalAddonsRefreshUtc = DateTime.MinValue;
        private DateTime _noticeExpiresAt;
        private TimeSpan _noticeDuration = TimeSpan.Zero;

        public List<CharacterOption> CharacterOptions { get; private set; } = [];
        public List<CharacterOption> FolderOptions { get; private set; } = [];
        public List<CharacterOption> GroupOptions { get; private set; } = [];
        public List<CharacterOption> ProfileOptions { get; private set; } = [];
        public List<CharacterOption> BackupOptions { get; private set; } = [];
        public List<CharacterOption> SavedPresetOptions { get; private set; } = [];
        public List<CharacterOption> RecentPresetOptions { get; private set; } = [];

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
            ConfigureBuildVisibility();
            ResetTopStatus();
            _noticeTimer.Interval = NoticeTickInterval;
            _noticeTimer.Tick += NoticeTimer_Tick;
            _gameMonitorTimer.Interval = TimeSpan.FromSeconds(8);
            _gameMonitorTimer.Tick += GameMonitorTimer_Tick;
            Loaded += async (_, _) =>
            {
                LoadMods();
                await CheckForUpdatesAsync(showWhenCurrent: false);
            };
            Activated += (_, _) => RefreshPhysicalAddonsStateIfDiagnosticsOpen();
            Closing += (_, _) => ShutdownApplicationServices();
            Closed += (_, _) => Application.Current.Shutdown();
        }

        private void ConfigureBuildVisibility()
        {
            AddonsDiagnosticsExpander.Visibility = IsDevBuild
                ? Visibility.Visible
                : Visibility.Collapsed;
            ReloadButton.Visibility = IsDevBuild
                ? Visibility.Visible
                : Visibility.Collapsed;
            DevToolsPanel.Visibility = IsDevBuild
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void LoadMods()
        {
            _isLoading = true;
            _preferences = UserPreferenceService.Load(_preferencesPath);
            RefreshBackupOptions();
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
            _allMods = snapshot.AllMods
                .OrderBy(mod => mod.Hero)
                .ThenBy(mod => mod.Name)
                .ToList();
            _mods = snapshot.Mods
                .Where(mod => mod.IsInSelectedProfile)
                .OrderBy(mod => mod.Hero)
                .ThenBy(mod => mod.Name)
                .ToList();

            _addonsState = AddonsInventoryService.ApplyPhysicalState(_gamePath, _mods);
            UserPreferenceService.Apply(_mods, _preferences, _selectedProfileId);
            SyncVisibleRandomizerSettingsToAllMods();
            var profilePreferences = GetCurrentProfilePreferences();
            _currentLoadout = profilePreferences.LastSessionLoadout;
            ApplyLoadout(_currentLoadout);
            _expandedSectionKey = profilePreferences.ExpandedSections?.FirstOrDefault() ?? "";
            RefreshCharacterOptions();
            RefreshFolderOptions();
            RefreshGroupOptions();
            RefreshProfileOptions();
            RefreshBackupOptions();
            RefreshLoadoutSummary();
            RefreshPresetOptions();
            RefreshAddonsDiagnostics();
            ApplyNonRandomizerExclusions();
            BindGroups();

            SetNotice(
                "Profile loaded",
                BuildLoadedStatus(_addonsState),
                NoticeKind.Success);
            LogDiagnosticSnapshot("Profile loaded", BuildLoadedStatus(_addonsState));
            _discordPresence.ShowLoaded(GetSelectedProfileName(), _mods.Count, _mods.Count(mod => mod.Enabled));
            StatePathText.Text = _statePath;
            _isLoading = false;
        }

        private void RandomiseButton_Click(object sender, RoutedEventArgs e)
        {
            RandomiseLoadout(showNotice: true);
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ValidateApplyInputs(requireGamePath: false);
                if (!EnsureDlmmClosedBeforeGameWrite("Apply to Game"))
                    return;

                ApplyLoadoutToGame(showSuccessNotice: true);
            }
            catch (Exception ex)
            {
                LogDiagnosticSnapshot("Apply failed", $"Apply failed for {GetSelectedProfileName()}: {ex.Message}", ex);
                SetNotice("Apply failed", $"Apply failed for {GetSelectedProfileName()}: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
        }

        private async void RandomisePlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ValidateApplyInputs(requireGamePath: true);
                if (!EnsureDlmmClosedBeforeGameWrite("Randomise, Apply & Play"))
                    return;

                if (IsDeadlockRunning())
                {
                    SetNotice(
                        "Close Deadlock first",
                        "Deadlock is already running. Close it, then use Randomise, Apply & Play so the staged skins load cleanly.",
                        NoticeKind.Warning,
                        showBanner: true);
                    return;
                }

                RandomisePlayButton.IsEnabled = false;
                RandomiseLoadout(showNotice: false);
                var result = ApplyLoadoutToGame(showSuccessNotice: false);
                if (result is null)
                    return;

                if (result.RequiresDlmmApply)
                {
                    LogDiagnosticSnapshot("Randomise, Apply & Play pending DLMM apply", $"Randomised {_currentLoadout.Count} picks and updated {GetSelectedProfileName()}. DLMM apply/rebuild is required.");
                    SetNotice(
                        "DLMM apply needed",
                        $"Randomised {_currentLoadout.Count} picks and updated {GetSelectedProfileName()}. Apply/rebuild in DLMM before launching Deadlock.",
                        NoticeKind.Warning,
                        showBanner: true);
                    return;
                }

                SetNotice("Launching soon", "Applied the loadout. Giving staged files a moment before launching Deadlock.", NoticeKind.Info, showBanner: true);
                await Task.Delay(GameLaunchSettleDelay);

                LaunchGame("Randomise, Apply & Play launched", $"Applied {_currentLoadout.Count} current picks to {GetSelectedProfileName()} and launched Deadlock.", "Applied the current loadout and launched Deadlock. Use Randomise before launch when you want a different set.");
            }
            catch (Exception ex)
            {
                LogDiagnosticSnapshot("Randomise, Apply & Play failed", $"Randomise, Apply & Play failed: {ex.Message}", ex);
                SetNotice("Launch failed", $"Randomise, Apply & Play failed: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
            finally
            {
                RandomisePlayButton.IsEnabled = true;
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ValidateApplyInputs(requireGamePath: true);
                LaunchGame("Play launched", $"Launched Deadlock from {GetSelectedProfileName()} without changing the current loadout.", "Launched Deadlock without randomising or applying changes.");
            }
            catch (Exception ex)
            {
                LogDiagnosticSnapshot("Play failed", $"Could not launch Deadlock without randomising: {ex.Message}", ex);
                SetNotice("Launch failed", $"Could not launch Deadlock: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            LoadMods();
        }

        private void RemoveModFromDlmmButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: DlmmMod mod })
                return;

            if (string.IsNullOrWhiteSpace(mod.RemoteId))
            {
                SetNotice("Cannot remove mod", "This mod does not have a DLMM remoteId, so it cannot be removed from the DLMM profile.", NoticeKind.Warning, showBanner: true);
                return;
            }

            var confirmation = MessageBox.Show(
                $"Remove '{mod.Name}' from {GetSelectedProfileName()} in DLMM?\n\nThis removes it from the selected DLMM profile, but does not delete downloaded mod files.",
                "Remove mod from DLMM",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
                return;

            try
            {
                ValidateApplyInputs(requireGamePath: false);
                var backupPath = DlmmStateService.RemoveProfileMod(_statePath, _selectedProfileId, mod.RemoteId);
                UserPreferenceService.RemoveMod(_preferencesPath, _selectedProfileId, mod.RemoteId);
                _currentLoadout = _currentLoadout
                    .Where(pick => !string.Equals(pick.RemoteId, mod.RemoteId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                UserPreferenceService.SaveLastSessionLoadout(_preferencesPath, _selectedProfileId, _currentLoadout);
                LogDiagnosticSnapshot("Mod removed from DLMM", $"Removed {mod.Name} ({mod.RemoteId}) from {GetSelectedProfileName()}. Backup: {backupPath}");
                var profileName = GetSelectedProfileName();
                var modName = mod.Name;
                LoadMods();
                SetNotice("Mod removed", $"Removed {modName} from {profileName} in DLMM. Backup created.", NoticeKind.Success, showBanner: true);
            }
            catch (Exception ex)
            {
                LogDiagnosticSnapshot("Remove mod failed", $"Could not remove {mod.Name} from DLMM: {ex.Message}", ex);
                SetNotice("Remove failed", $"Could not remove {mod.Name} from DLMM: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
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

        private void BackupPreferencesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AutoSavePreferences(showStatus: false);
                var backupPath = UserPreferenceService.Backup(_preferencesPath);
                RefreshBackupOptions();
                BackupBox.SelectedValue = backupPath;
                LogDiagnosticSnapshot("Backup created", $"Saved app setup backup: {backupPath}");
                SetNotice("Backup created", $"Saved app setup backup: {backupPath}", NoticeKind.Success, showBanner: true);
            }
            catch (Exception ex)
            {
                LogDiagnosticSnapshot("Backup failed", $"Could not back up app setup: {ex.Message}", ex);
                SetNotice("Backup failed", $"Could not back up app setup: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
        }

        private void RepairStagedVpksButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ValidateApplyInputs(requireGamePath: true);
                if (!EnsureDlmmClosedBeforeGameWrite("Reset app-staged VPKs"))
                    return;

                if (IsDeadlockRunning())
                {
                    SetNotice(
                        "Close Deadlock first",
                        "Close Deadlock before repairing staged VPKs so the game is not reading files while they are removed.",
                        NoticeKind.Warning,
                        showBanner: true);
                    return;
                }

                var confirmation = MessageBox.Show(
                    "Reset app-staged VPKs?\n\nThis moves randomiser skin source VPKs into this app's local vault so Deadlock cannot load every downloaded skin at once, then removes known randomiser live slots and app-staged files. Folder, UI, HUD, unknown, and non-randomiser mods are left alone.\n\nAfter this, apply/rebuild in DLMM to restore DLMM-managed folder, UI, HUD, and model mods.",
                    "Reset app-staged VPKs",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes)
                    return;

                var result = ManifestGameModStagingService.RepairAppStagedVpks(_statePath, _gamePath, GetSourceVaultMods());
                _addonsState = AddonsInventoryService.ApplyPhysicalState(_gamePath, _mods);
                RefreshAddonsDiagnostics();
                LogDiagnosticSnapshot("Repair app-staged VPKs", BuildRepairStatus(result));
                SetNotice("Repair complete", BuildRepairStatus(result), NoticeKind.Success, showBanner: true);
            }
            catch (Exception ex)
            {
                LogDiagnosticSnapshot("Repair app-staged VPKs failed", $"Could not repair app-staged VPKs: {ex.Message}", ex);
                SetNotice("Repair failed", $"Could not repair app-staged VPKs: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(showWhenCurrent: true);
        }

        private void OpenReleasesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(UpdateService.ReleasesPageUrl);
        }

        private async Task CheckForUpdatesAsync(bool showWhenCurrent)
        {
            try
            {
                var result = await UpdateService.CheckForUpdatesAsync();
                _latestUpdate = result;
                DownloadUpdateButton.IsEnabled = result.UpdateAvailable && result.HasPortablePackage;

                if (result.UpdateAvailable)
                {
                    UpdateStatusText.Text = result.HasPortablePackage
                        ? $"Version {result.LatestVersion} is available. Install it here without keeping setup files."
                        : $"Version {result.LatestVersion} is available, but no portable update package was found on the release.";
                    SetNotice(
                        "Update available",
                        $"Version {result.LatestVersion} is available. Current version: {result.CurrentVersion}.",
                        NoticeKind.Info,
                        showBanner: true);
                    return;
                }

                UpdateStatusText.Text = $"You are on the latest release: {result.CurrentVersion}.";
                if (showWhenCurrent)
                    SetNotice("Up to date", $"You are on the latest release: {result.CurrentVersion}.", NoticeKind.Success, showBanner: true);
            }
            catch (Exception ex)
            {
                _latestUpdate = null;
                DownloadUpdateButton.IsEnabled = false;
                UpdateStatusText.Text = "Could not check GitHub Releases. If the repo is private, update checks only work after the repo is public.";
                if (showWhenCurrent)
                    SetNotice("Update check failed", $"Could not check GitHub Releases: {ex.Message}", NoticeKind.Warning, showBanner: true);
            }
        }

        private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_latestUpdate is null || !_latestUpdate.HasPortablePackage)
            {
                SetNotice("No update package", "Check for updates first. The latest release needs a portable update package.", NoticeKind.Warning, showBanner: true);
                return;
            }

            try
            {
                DownloadUpdateButton.IsEnabled = false;
                UpdateStatusText.Text = $"Installing {_latestUpdate.PortablePackageName}...";
                await UpdateService.PrepareAndLaunchPortableUpdateAsync(_latestUpdate);
                UpdateStatusText.Text = "Installing update. The app will restart when it is done.";
                SetNotice("Installing update", "The app will close, update itself, and restart. No setup file will be left in Downloads.", NoticeKind.Success, showBanner: true);
                await Task.Delay(1000);
                ShutdownApplicationServices();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                DownloadUpdateButton.IsEnabled = _latestUpdate?.HasPortablePackage == true;
                UpdateStatusText.Text = "Update failed.";
                SetNotice("Update failed", $"Could not install the update: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
        }

        private void ShutdownApplicationServices()
        {
            if (_isShuttingDown)
                return;

            _isShuttingDown = true;
            _noticeTimer.Stop();
            _gameMonitorTimer.Stop();

            try
            {
                _discordPresence.Dispose();
            }
            catch
            {
                // Shutdown should never be blocked by Discord IPC cleanup.
            }
        }

        private void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
        {
            var backupPath = BackupBox.SelectedValue as string ?? "";
            if (string.IsNullOrWhiteSpace(backupPath))
            {
                SetNotice("Choose a backup", "Choose a setup backup to restore.", NoticeKind.Warning, showBanner: true);
                return;
            }

            try
            {
                var beforeRestoreBackup = UserPreferenceService.RestoreBackup(_preferencesPath, backupPath);
                _preferences = UserPreferenceService.Load(_preferencesPath);
                RefreshBackupOptions();
                LoadMods();
                SetNotice(
                    "Backup restored",
                    string.IsNullOrWhiteSpace(beforeRestoreBackup)
                        ? "Restored app setup backup."
                        : $"Restored app setup backup. Previous setup was backed up first: {beforeRestoreBackup}",
                    NoticeKind.Success,
                    showBanner: true);
            }
            catch (Exception ex)
            {
                SetNotice("Restore failed", $"Could not restore backup: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
        }

        private void ImportBackupButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import setup backup",
                Filter = "Setup backup JSON|*.json|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var importedPath = UserPreferenceService.ImportBackup(dialog.FileName);
                RefreshBackupOptions();
                BackupBox.SelectedValue = importedPath;
                LogDiagnosticSnapshot("Backup imported", $"Imported app setup backup: {importedPath}");
                SetNotice("Backup imported", "Imported setup backup. Choose Restore when you want to use it.", NoticeKind.Success, showBanner: true);
            }
            catch (Exception ex)
            {
                SetNotice("Import failed", $"Could not import setup backup: {ex.Message}", NoticeKind.Error, showBanner: true);
            }
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
            RefreshGroupOptions();
            BindGroups();
            AutoSavePreferences(showStatus: false);
            SetNotice("Folder added", $"Added folder {folderName.Trim()}.", NoticeKind.Success);
        }

        private void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folder = EditFolderBox.SelectedValue as string ?? "";
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
            EditFolderBox.SelectedValue = null;
            EditFolderNameTextBox.Text = "";
            RefreshFolderOptions();
            RefreshGroupOptions();
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
            RefreshGroupOptions();
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

        private void RequestBindGroups()
        {
            if (!IsLoaded || _isLoading)
                return;

            if (_isBindingGroups)
            {
                _bindGroupsAgain = true;
                return;
            }

            BindGroups();
        }

        private void BindGroups()
        {
            _isBindingGroups = true;

            var filter = NormalizeFilter(CharacterFilterBox.Text);
            var folderFilter = NormalizeFolder(FolderFilterBox.Text);
            var searchText = NormalizeSearch(ModSearchBox.Text);
            var searchIsActive = !string.IsNullOrWhiteSpace(searchText);
            var inUseOnly = InUseFilterCheckBox.IsChecked == true;
            var inRandomizerOnly = InRandomizerFilterCheckBox.IsChecked == true;
            var shouldAutoExpandSections =
                !string.IsNullOrWhiteSpace(filter)
                || !string.IsNullOrWhiteSpace(folderFilter)
                || searchIsActive;
            var groupedMods = _mods
                .Where(mod => CharacterMatchesFilter(mod, filter))
                .Where(mod => FolderMatchesFilter(mod, folderFilter))
                .Where(mod => !inUseOnly || mod.Enabled)
                .Where(mod => !inRandomizerOnly || mod.IncludedInRandomizer)
                .Where(mod => SearchMatchesFilter(mod, searchText))
                .GroupBy(mod => searchIsActive ? "search-results" : GetSectionKey(mod))
                .Select(group => new HeroModGroup
                {
                    Hero = group.Key,
                    IsSearchResults = searchIsActive,
                    IsFolder = !searchIsActive && group.Any(mod => !string.IsNullOrWhiteSpace(NormalizeFolder(mod.Folder))),
                    DisplayName = searchIsActive
                        ? "Search results"
                        : group.Any(mod => !string.IsNullOrWhiteSpace(NormalizeFolder(mod.Folder)))
                        ? GetFolderDisplayName(group.Key)
                        : "",
                    IsExpanded = shouldAutoExpandSections || ShouldExpandGroup(group.Key),
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
                if (!_bindGroupsAgain)
                    return;

                _bindGroupsAgain = false;
                BindGroups();
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private bool RandomiseCurrentLoadout()
        {
            SyncVisibleRandomizerSettingsToAllMods();
            HashSet<string> stageableRemoteIds = !string.IsNullOrWhiteSpace(_gamePath)
                ? ManifestGameModStagingService.GetStageableRemoteIds(_gamePath, GetSourceVaultMods())
                : [];

            _mods = ModSelectionService.RandomlySelectOnePerHero(
                    _mods,
                    stageableRemoteIds.Count > 0 ? stageableRemoteIds : null,
                    _currentLoadout)
                .OrderBy(mod => mod.Hero)
                .ThenBy(mod => mod.Name)
                .ToList();

            _currentLoadout = BuildCurrentLoadout();
            var matchedExistingPreset = UserPreferenceService.SaveLastSessionLoadout(_preferencesPath, _selectedProfileId, _currentLoadout);
            _preferences = UserPreferenceService.Load(_preferencesPath);
            RefreshLoadoutSummary();
            RefreshPresetOptions();
            BindGroups();
            AutoSavePreferences(showStatus: false);
            return matchedExistingPreset;
        }

        private bool RandomiseLoadout(bool showNotice)
        {
            var matchedExistingPreset = RandomiseCurrentLoadout();
            _discordPresence.ShowRerolled(GetSelectedProfileName(), _currentLoadout.Count);

            if (!showNotice)
                return matchedExistingPreset;

            if (matchedExistingPreset)
            {
                SetNotice("Preset matched", "This randomised loadout matched a recent preset, so its timestamp was updated and moved to the top.", NoticeKind.Info, showBanner: true);
                return matchedExistingPreset;
            }

            LogDiagnosticSnapshot("Randomise", $"Randomised {_currentLoadout.Count} hero picks.");
            SetNotice("Randomise ready", $"Randomised {_currentLoadout.Count} hero picks. Apply to Game, then Play when you are happy with them.", NoticeKind.Success, showBanner: true);
            return matchedExistingPreset;
        }

        private ApplyResult? ApplyLoadoutToGame(bool showSuccessNotice)
        {
            UserPreferenceService.Save(_preferencesPath, _mods, _statePath, _selectedProfileId);
            var result = ModApplyService.Apply(_statePath, _gamePath, _mods, _selectedProfileId, GetSourceVaultMods());
            if (result.WrittenCount == 0)
            {
                SetNotice("Nothing applied", $"No mods were written to {GetSelectedProfileName()} because this profile has no visible mod entries.", NoticeKind.Warning, showBanner: true);
                return null;
            }

            _addonsState = AddonsInventoryService.ApplyPhysicalState(_gamePath, _mods);
            RefreshAddonsDiagnostics();
            if (result.EnabledModsWithoutStagedFilesCount > 0)
            {
                SetNotice("Some skins were not staged", BuildMissingStagedFilesStatus(result), NoticeKind.Warning, showBanner: true);
                return null;
            }

            var applyStatus = BuildApplyStatus(result);
            LogDiagnosticSnapshot("Apply complete", applyStatus);
            _discordPresence.ShowApplied(GetSelectedProfileName(), result.EnabledCount);
            if (showSuccessNotice)
                SetNotice("Apply complete", applyStatus, NoticeKind.Success, showBanner: true);

            return result;
        }

        private void LaunchGame(string diagnosticAction, string diagnosticMessage, string successMessage)
        {
            GameLaunchService.Launch(_gamePath);
            LogDiagnosticSnapshot(diagnosticAction, diagnosticMessage);
            _discordPresence.ShowPlaying(GetSelectedProfileName(), _currentLoadout.Count);
            StartGamePresenceWatch();
            SetNotice("Deadlock launched", successMessage, NoticeKind.Success, showBanner: true);
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

        private void RefreshPresetOptions()
        {
            var profilePreferences = GetCurrentProfilePreferences();
            var savedPresets = profilePreferences.SavedPresets
                .OrderByDescending(preset => preset.CreatedAt)
                .Select(preset => new CharacterOption
                {
                    Key = preset.Id,
                    Name = $"{preset.Name} - {preset.PickCountText}"
                })
                .ToList();
            var recentPresets = profilePreferences.RecentSessionPresets
                .OrderByDescending(preset => preset.CreatedAt)
                .Take(5)
                .Select(preset => new CharacterOption
                {
                    Key = preset.Id,
                    Name = $"{preset.CreatedAt:dd MMM HH:mm:ss} - {preset.PickCountText}"
                })
                .ToList();

            RefreshPresetBox(
                SavedPresetBox,
                savedPresets,
                ref _savedPresetOptionsSignature,
                options => SavedPresetOptions = options);
            RefreshPresetBox(
                RecentPresetBox,
                recentPresets,
                ref _recentPresetOptionsSignature,
                options => RecentPresetOptions = options);
        }

        private static void RefreshPresetBox(
            ComboBox presetBox,
            List<CharacterOption> presets,
            ref string optionsSignature,
            Action<List<CharacterOption>> setOptions)
        {
            var signature = BuildOptionSignature(presets);
            if (string.Equals(signature, optionsSignature, StringComparison.Ordinal))
                return;

            var selectedPreset = presetBox.SelectedValue as string;
            optionsSignature = signature;
            setOptions(presets);
            presetBox.ItemsSource = presets;
            presetBox.SelectedValue = presets.Any(option => string.Equals(option.Key, selectedPreset, StringComparison.OrdinalIgnoreCase))
                ? selectedPreset
                : presets.FirstOrDefault()?.Key;
        }

        private void RefreshAddonsDiagnostics()
        {
            AddonsDiagnosticsList.ItemsSource = _addonsState.Diagnostics;
            AddonsDiagnosticsSummaryText.Text =
                $"{_addonsState.LiveSlotCount} live VPKs • {_addonsState.AppStagedModCount} app staged • {_addonsState.LogMatchedModCount} DLMM log matched • {_addonsState.HashMatchedModCount} hash matched • {_addonsState.ConfirmedModCount + _addonsState.ProfileDisambiguatedModCount} likely live • {_addonsState.SlotOnlyGuessCount} weak guesses • {_addonsState.UnmatchedLiveSlotCount} unmatched • {_addonsState.StateOnlyModCount} old DLMM flags";
        }

        private void RefreshPhysicalAddonsStateIfDiagnosticsOpen(bool force = false)
        {
            if (_isLoading || !AddonsDiagnosticsExpander.IsExpanded)
                return;

            if (!force && DateTime.UtcNow - _lastPhysicalAddonsRefreshUtc < TimeSpan.FromSeconds(2))
                return;

            _addonsState = AddonsInventoryService.ApplyPhysicalState(_gamePath, _mods);
            _lastPhysicalAddonsRefreshUtc = DateTime.UtcNow;
            RefreshAddonsDiagnostics();
        }

        private void AddonsDiagnosticsExpander_Expanded(object sender, RoutedEventArgs e)
        {
            RefreshPhysicalAddonsStateIfDiagnosticsOpen(force: true);
        }

        private IReadOnlyCollection<DlmmMod> GetSourceVaultMods()
        {
            return _allMods.Count > 0
                ? _allMods
                : _mods;
        }

        private void SyncVisibleRandomizerSettingsToAllMods()
        {
            if (_allMods.Count == 0 || _mods.Count == 0)
                return;

            var visibleModsByRemoteId = _mods
                .Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId))
                .GroupBy(mod => mod.RemoteId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var mod in _allMods)
            {
                if (string.IsNullOrWhiteSpace(mod.RemoteId)
                    || !visibleModsByRemoteId.TryGetValue(mod.RemoteId, out var visibleMod))
                {
                    continue;
                }

                mod.Folder = visibleMod.Folder;
                mod.Hero = visibleMod.Hero;
                mod.IncludedInRandomizer = visibleMod.IncludedInRandomizer;
            }
        }

        private void LogDiagnosticSnapshot(string action, string message = "", Exception? exception = null)
        {
            AppDiagnosticLogService.WriteSnapshot(
                action,
                _statePath,
                _gamePath,
                _selectedProfileId,
                GetSelectedProfileName(),
                _mods,
                _currentLoadout,
                _addonsState,
                message,
                exception);
        }

        private void SavePresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentLoadout.Count == 0)
            {
                SetNotice("No preset to save", "Randomise or apply a loadout before saving it as a preset.", NoticeKind.Warning, showBanner: true);
                return;
            }

            var name = PresetNameTextBox.Text.Trim();
            UserPreferenceService.SavePreset(_preferencesPath, _selectedProfileId, name, _currentLoadout);
            _preferences = UserPreferenceService.Load(_preferencesPath);
            PresetNameTextBox.Text = "";
            RefreshPresetOptions();
            SetNotice("Preset saved", $"Saved {(_currentLoadout.Count == 1 ? "1 pick" : $"{_currentLoadout.Count} picks")} as a preset.", NoticeKind.Success, showBanner: true);
        }

        private void LoadSavedPresetButton_Click(object sender, RoutedEventArgs e)
        {
            LoadPresetFromBox(SavedPresetBox, "saved preset");
        }

        private void LoadRecentPresetButton_Click(object sender, RoutedEventArgs e)
        {
            LoadPresetFromBox(RecentPresetBox, "recent session");
        }

        private void LoadPresetFromBox(ComboBox presetBox, string presetKind)
        {
            var presetId = presetBox.SelectedValue as string ?? "";
            var preset = FindPreset(presetId);
            if (preset is null)
            {
                SetNotice("Choose a preset", $"Choose a {presetKind} to load.", NoticeKind.Warning, showBanner: true);
                return;
            }

            ApplyPreset(preset);
            _currentLoadout = BuildCurrentLoadout();
            UserPreferenceService.SaveLastSessionLoadout(_preferencesPath, _selectedProfileId, _currentLoadout);
            _preferences = UserPreferenceService.Load(_preferencesPath);
            RefreshLoadoutSummary();
            RefreshPresetOptions();
            BindGroups();
            AutoSavePreferences(showStatus: false);
            _discordPresence.ShowRerolled(GetSelectedProfileName(), _currentLoadout.Count);
            SetNotice("Preset loaded", $"Loaded {preset.Name}. Apply when you are ready.", NoticeKind.Success, showBanner: true);
        }

        private LoadoutPreset? FindPreset(string presetId)
        {
            var profilePreferences = GetCurrentProfilePreferences();
            return profilePreferences.SavedPresets
                .Concat(profilePreferences.RecentSessionPresets)
                .FirstOrDefault(preset => string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase));
        }

        private void ApplyPreset(LoadoutPreset preset)
        {
            ApplyLoadout(preset.Picks);
        }

        private void ApplyLoadout(IReadOnlyCollection<LoadoutPick> picks)
        {
            var selectedRemoteIds = picks
                .Select(pick => pick.RemoteId)
                .Where(remoteId => !string.IsNullOrWhiteSpace(remoteId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var heroes = picks
                .Select(pick => NormalizeHero(pick.Hero))
                .Where(hero => !string.IsNullOrWhiteSpace(hero) && hero != "unknown")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in _mods)
            {
                if (selectedRemoteIds.Contains(mod.RemoteId))
                {
                    mod.Enabled = true;
                    mod.Folder = "";
                    mod.IncludedInRandomizer = true;
                    continue;
                }

                if (heroes.Contains(NormalizeHero(mod.Hero))
                    && mod.IncludedInRandomizer
                    && string.IsNullOrWhiteSpace(mod.Folder))
                {
                    mod.Enabled = false;
                }
            }
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

            var selectedEditFolder = EditFolderBox.SelectedValue as string;
            EditFolderBox.ItemsSource = FolderOptions
                .Where(option => !string.IsNullOrWhiteSpace(option.Key))
                .ToList();
            EditFolderBox.SelectedValue = selectedEditFolder;
        }

        private void RefreshGroupOptions()
        {
            List<CharacterOption> nextGroupOptions =
            [
                ..CharacterOptions.Select(option => new CharacterOption
                {
                    Key = $"hero:{option.Key}",
                    Name = option.Name
                }),
                ..FolderOptions
                    .Where(option => !string.IsNullOrWhiteSpace(option.Key))
                    .Select(option => new CharacterOption
                    {
                        Key = $"folder:{option.Key}",
                        Name = $"Folder: {option.Name}"
                    })
            ];

            var signature = BuildOptionSignature(nextGroupOptions);
            if (string.Equals(signature, _groupOptionsSignature, StringComparison.Ordinal))
                return;

            _groupOptionsSignature = signature;
            GroupOptions = nextGroupOptions;
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

        private void RefreshBackupOptions()
        {
            var selectedBackup = BackupBox.SelectedValue as string;
            BackupLocationText.Text = $"Setup backups are saved in {UserPreferenceService.DefaultUserBackupDirectory}";
            BackupOptions = UserPreferenceService.ListBackups();
            BackupBox.ItemsSource = BackupOptions;
            BackupBox.SelectedValue = BackupOptions.Any(option => string.Equals(option.Key, selectedBackup, StringComparison.OrdinalIgnoreCase))
                ? selectedBackup
                : BackupOptions.FirstOrDefault()?.Key;
        }

        private void CharacterFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isLoading)
                return;

            if (CharacterFilterBox.SelectedItem is string selectedCharacter)
                CharacterFilterBox.Text = selectedCharacter;

            Dispatcher.BeginInvoke(RequestBindGroups);
        }

        private void CharacterFilterBox_DropDownClosed(object sender, EventArgs e)
        {
            RequestBindGroups();
        }

        private void CharacterFilterBox_KeyUp(object sender, KeyEventArgs e)
        {
            RequestBindGroups();
        }

        private void FolderFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isLoading)
                return;

            if (FolderFilterBox.SelectedItem is string selectedFolder)
                FolderFilterBox.Text = selectedFolder;

            Dispatcher.BeginInvoke(RequestBindGroups);
        }

        private void FolderFilterBox_DropDownClosed(object sender, EventArgs e)
        {
            RequestBindGroups();
        }

        private void FolderFilterBox_KeyUp(object sender, KeyEventArgs e)
        {
            RequestBindGroups();
        }

        private void ModSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RequestBindGroups();
        }

        private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(RequestBindGroups);
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

        private void GroupSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBindingGroups || _isLoading)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                DlmmMod? changedMod = null;
                if (sender is ComboBox { DataContext: DlmmMod mod })
                {
                    changedMod = mod;
                }

                ApplyNonRandomizerExclusions(changedMod);
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
                EnforceSingleEnabledSkinPerHero(mod);
                _currentLoadout = BuildCurrentLoadout();
                RefreshLoadoutSummary();
                RequestBindGroups();
                AutoSavePreferences(mod: mod);
                return;
            }

            ApplyNonRandomizerExclusions();
            EnforceSingleEnabledSkinPerHero();
            _currentLoadout = BuildCurrentLoadout();
            RefreshLoadoutSummary();
            RequestBindGroups();
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

        private void EnforceSingleEnabledSkinPerHero(DlmmMod? preferredMod = null)
        {
            if (preferredMod is { Enabled: true } && IsRandomizerSkin(preferredMod))
            {
                var preferredHero = NormalizeHero(preferredMod.Hero);
                foreach (var otherMod in _mods.Where(mod =>
                             !ReferenceEquals(mod, preferredMod)
                             && IsRandomizerSkin(mod)
                             && mod.Enabled
                             && string.Equals(NormalizeHero(mod.Hero), preferredHero, StringComparison.OrdinalIgnoreCase)))
                {
                    otherMod.Enabled = false;
                    UserPreferenceService.SaveMod(_preferencesPath, otherMod, _statePath, _selectedProfileId);
                }

                return;
            }

            foreach (var heroGroup in _mods
                         .Where(mod => IsRandomizerSkin(mod) && mod.Enabled)
                         .GroupBy(mod => NormalizeHero(mod.Hero))
                         .Where(group => group.Count() > 1))
            {
                var enabledMods = heroGroup.ToList();
                var keptMod = enabledMods[Random.Shared.Next(enabledMods.Count)];
                foreach (var mod in enabledMods.Where(mod => !ReferenceEquals(mod, keptMod)))
                {
                    mod.Enabled = false;
                    UserPreferenceService.SaveMod(_preferencesPath, mod, _statePath, _selectedProfileId);
                }
            }
        }

        private static bool IsRandomizerSkin(DlmmMod? mod)
        {
            return mod is not null
                && mod.IncludedInRandomizer
                && string.IsNullOrWhiteSpace(mod.Folder)
                && NormalizeHero(mod.Hero) != "unknown";
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
            OpenUrl(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
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

            // The progress bar value is animated directly; the timer only hides the banner when time expires.
        }

        private void StartGamePresenceWatch()
        {
            _isWatchingLaunchedGame = true;
            _discordShowsInGame = false;
            _gameMonitorTimer.Stop();
            _gameMonitorTimer.Start();
            UpdateGamePresence();
        }

        private void GameMonitorTimer_Tick(object? sender, EventArgs e)
        {
            UpdateGamePresence();
        }

        private void UpdateGamePresence()
        {
            if (!_isWatchingLaunchedGame)
                return;

            if (IsDeadlockRunning())
            {
                if (_discordShowsInGame)
                    return;

                _discordPresence.ShowInGame(GetSelectedProfileName(), _currentLoadout.Count);
                _discordShowsInGame = true;
                return;
            }

            if (!_discordShowsInGame)
                return;

            _gameMonitorTimer.Stop();
            _isWatchingLaunchedGame = false;
            _discordShowsInGame = false;
            _discordPresence.ShowLoaded(GetSelectedProfileName(), _mods.Count, _mods.Count(mod => mod.Enabled));
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
                HideNoticeBanner(resetTopStatus: false);
        }

        private void ShowNoticeBanner(NoticeKind kind)
        {
            _noticeDuration = NoticeDisplayDuration;

            _noticeExpiresAt = DateTime.UtcNow.Add(_noticeDuration);
            NoticeExpiryBar.BeginAnimation(RangeBase.ValueProperty, null);
            NoticeExpiryBar.Value = 1;
            NoticeBanner.BeginAnimation(OpacityProperty, null);
            NoticeBanner.BeginAnimation(MaxHeightProperty, null);
            NoticeBannerTransform.BeginAnimation(TranslateTransform.YProperty, null);
            NoticeBanner.Visibility = Visibility.Visible;
            NoticeBanner.Opacity = 0;
            NoticeBanner.MaxHeight = 0;
            NoticeBannerTransform.Y = -8;
            AnimateNoticeBanner(isVisible: true);
            NoticeExpiryBar.BeginAnimation(
                RangeBase.ValueProperty,
                new DoubleAnimation
                {
                    From = 1,
                    To = 0,
                    Duration = _noticeDuration,
                    EasingFunction = null
                });
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        private void HideNoticeBanner(bool resetTopStatus = true)
        {
            _noticeTimer.Stop();
            NoticeExpiryBar.BeginAnimation(RangeBase.ValueProperty, null);
            NoticeExpiryBar.Value = 0;
            if (resetTopStatus)
                ResetTopStatus();

            if (NoticeBanner.Visibility != Visibility.Visible)
                return;

            AnimateNoticeBanner(isVisible: false);
        }

        private void AnimateNoticeBanner(bool isVisible)
        {
            var opacityAnimation = new DoubleAnimation
            {
                To = isVisible ? 1 : 0,
                Duration = NoticeAnimationDuration,
                EasingFunction = new CubicEase { EasingMode = isVisible ? EasingMode.EaseOut : EasingMode.EaseIn }
            };
            var offsetAnimation = new DoubleAnimation
            {
                To = isVisible ? 0 : -8,
                Duration = NoticeAnimationDuration,
                EasingFunction = new CubicEase { EasingMode = isVisible ? EasingMode.EaseOut : EasingMode.EaseIn }
            };

            if (!isVisible)
            {
                opacityAnimation.Completed += (_, _) =>
                {
                    NoticeBanner.Visibility = Visibility.Collapsed;
                    NoticeBanner.Opacity = 0;
                    NoticeBanner.MaxHeight = 0;
                    NoticeBannerTransform.Y = -8;
                };
            }

            var heightAnimation = new DoubleAnimation
            {
                From = isVisible ? 0 : Math.Max(0, NoticeBanner.ActualHeight),
                To = isVisible ? 260 : 0,
                Duration = NoticeAnimationDuration,
                EasingFunction = new CubicEase { EasingMode = isVisible ? EasingMode.EaseOut : EasingMode.EaseInOut }
            };

            NoticeBanner.BeginAnimation(OpacityProperty, opacityAnimation);
            NoticeBanner.BeginAnimation(MaxHeightProperty, heightAnimation);
            NoticeBannerTransform.BeginAnimation(TranslateTransform.YProperty, offsetAnimation);
        }

        private void ResetTopStatus()
        {
            NoticeTitleText.Text = "Deadlock Skin Randomiser";
            StatusText.Text = $"Version {_appVersion}";
            TrayStatusDot.Fill = BrushFrom("#91D18B");
        }

        private static SolidColorBrush BrushFrom(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        }

        private static string GetAppVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            var version = string.IsNullOrWhiteSpace(informationalVersion)
                ? assembly.GetName().Version?.ToString(3) ?? "0.1.0"
                : informationalVersion.Split('+')[0];

            return IsDevBuild ? $"{version} dev" : version;
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
            var status = result.GameFilesStaged
                ? $"Applied to game for {GetSelectedProfileName()}. Selected in app: {result.EnabledCount}. Staged +{result.StagedEnabledCount}/-{result.StagedDisabledCount}."
                : $"DLMM state updated for {GetSelectedProfileName()}. Selected in app: {result.EnabledCount}. Open DLMM and apply/rebuild before launching the game.";

            if (result.ForcedDisabledCount > 0)
                status += $" Disabled {result.ForcedDisabledCount} duplicate hero picks.";
            if (result.GameFilesStaged && result.StagingSkippedCount > 0)
                status += $" {result.StagingSkippedCount} mods had no game files to stage.";
            if (result.StaleSourceVpkSkippedCount > 0)
                status += $" Ignored {result.StaleSourceVpkSkippedCount} stale source VPKs from older mod versions.";
            if (result.EnabledModsWithoutStagedFilesCount > 0)
                status += $" {BuildMissingStagedFilesStatus(result)}";
            if (!string.IsNullOrWhiteSpace(result.BackupPath))
                status += " DLMM state backup created beside state.json.";
            if (result.GameFilesStaged && !string.IsNullOrWhiteSpace(result.AddonsBackupPath))
                status += " Staging manifest updated.";

            if (result.RequiresDlmmApply)
                status += " Direct VPK staging is disabled to avoid corrupting shared slots.";

            if (!IsDlmmRunning())
                return status;

            return result.GameFilesStaged
                ? $"{status} DLMM is open, but no DLMM rebuild is needed for app-staged skins."
                : $"{status} DLMM is open, so use DLMM's apply/rebuild step now.";
        }

        private string BuildLoadedStatus(AddonsReconciliationResult addonsState)
        {
            var selectedCount = _mods.Count(mod => mod.Enabled);
            if (addonsState.LiveSlotCount == 0)
                return $"Loaded {_mods.Count} mods from {GetSelectedProfileName()}. Selected in app: {selectedCount}. No live addon VPKs were found.";

            var status = $"Loaded {_mods.Count} mods from {GetSelectedProfileName()}. Selected in app: {selectedCount}. Addons has {addonsState.LiveSlotCount} live VPKs.";

            if (addonsState.AppStagedModCount > 0)
                status += $" {addonsState.AppStagedModCount} were staged by this app.";

            if (addonsState.LogMatchedModCount > 0)
                status += $" {addonsState.LogMatchedModCount} were matched from DLMM's apply log.";

            if (addonsState.HashMatchedModCount > 0)
                status += $" {addonsState.HashMatchedModCount} were matched by exact VPK hash.";

            if (addonsState.ProfileDisambiguatedModCount > 0)
                status += $" {addonsState.ProfileDisambiguatedModCount} shared slots were matched using DLMM profile state.";

            if (addonsState.SlotOnlyGuessCount > 0)
                status += $" {addonsState.SlotOnlyGuessCount} live slots only had weak slot-name guesses.";

            if (addonsState.AmbiguousLiveSlotCount > 0)
                status += $" {addonsState.AmbiguousLiveSlotCount} live slots are ambiguous.";

            if (addonsState.UnmatchedLiveSlotCount > 0)
                status += $" {addonsState.UnmatchedLiveSlotCount} live slots did not match a loaded mod.";

            if (addonsState.StateOnlyModCount > 0)
                status += $" {addonsState.StateOnlyModCount} old DLMM flags have no matching live files.";

            return status;
        }

        private static string BuildMissingStagedFilesStatus(ApplyResult result)
        {
            var names = result.EnabledModsWithoutStagedFiles
                .Take(4)
                .ToList();
            var listedNames = names.Count == 0
                ? $"{result.EnabledModsWithoutStagedFilesCount} selected skins"
                : string.Join(", ", names);
            var extraCount = result.EnabledModsWithoutStagedFilesCount - names.Count;
            if (extraCount > 0)
                listedNames += $" and {extraCount} more";

            return $"{listedNames} had no source VPK to stage. Open DLMM and apply/rebuild, or check diagnostics before launching.";
        }

        private static string BuildRepairStatus(StagingRepairResult result)
        {
            var status = result.RemovedLiveVpkCount == 0
                ? "No app-managed live VPKs needed removing."
                : $"Removed {result.RemovedLiveVpkCount} live VPK{(result.RemovedLiveVpkCount == 1 ? "" : "s")} during repair.";

            if (result.RemovedMatchedSkinVpkCount > 0)
                status += $" {result.RemovedMatchedSkinVpkCount} matched current randomiser skin source file{(result.RemovedMatchedSkinVpkCount == 1 ? "" : "s")}.";

            if (result.ExpectedDlmmManagedModCount > 0)
                status += $" Expected {result.ExpectedDlmmManagedModCount} DLMM-managed enabled mod{(result.ExpectedDlmmManagedModCount == 1 ? "" : "s")} to remain.";

            if (result.PreservedDlmmLiveVpkCount > 0)
                status += $" Preserved {result.PreservedDlmmLiveVpkCount} known DLMM live VPK{(result.PreservedDlmmLiveVpkCount == 1 ? "" : "s")}.";

            if (result.RemovedUnexpectedLiveVpkCount > 0)
            {
                var removed = string.Join(", ", result.RemovedUnexpectedLiveVpks.Take(3));
                var extra = result.RemovedUnexpectedLiveVpkCount > 3
                    ? $" and {result.RemovedUnexpectedLiveVpkCount - 3} more"
                    : "";
                status += $" Removed {result.RemovedUnexpectedLiveVpkCount} unexpected live VPK{(result.RemovedUnexpectedLiveVpkCount == 1 ? "" : "s")} ({removed}{extra}).";
            }

            if (result.MissingLiveVpkCount > 0)
                status += $" Cleaned {result.MissingLiveVpkCount} stale manifest entr{(result.MissingLiveVpkCount == 1 ? "y" : "ies")} for files that were already gone.";

            if (result.SkippedChangedLiveVpkCount > 0)
            {
                var skipped = string.Join(", ", result.SkippedLiveVpks.Take(3));
                var extra = result.SkippedChangedLiveVpkCount > 3
                    ? $" and {result.SkippedChangedLiveVpkCount - 3} more"
                    : "";
                status += $" Skipped {result.SkippedChangedLiveVpkCount} changed file{(result.SkippedChangedLiveVpkCount == 1 ? "" : "s")} ({skipped}{extra}) because they no longer match the app manifest.";
            }

            if (result.RemovedLiveVpkCount > 0 || result.MissingLiveVpkCount > 0)
                status += " Apply/rebuild in DLMM next to restore DLMM-managed folder, UI, HUD, and model mods.";

            if (!string.IsNullOrWhiteSpace(result.Preservation.BackupDirectory))
            {
                status += $" Repair safety backup created: {result.Preservation.BackupDirectory}.";
                if (result.Preservation.GameInfoBackupCount > 0)
                    status += $" Preserved {result.Preservation.GameInfoBackupCount} gameinfo file{(result.Preservation.GameInfoBackupCount == 1 ? "" : "s")}.";
                if (result.Preservation.DlmmLaunchSettingCount > 0)
                    status += $" Captured {result.Preservation.DlmmLaunchSettingCount} DLMM launch setting{(result.Preservation.DlmmLaunchSettingCount == 1 ? "" : "s")}.";
            }

            return status;
        }

        private static bool IsDlmmRunning()
        {
            return GetDlmmProcesses().Count > 0;
        }

        private static bool IsDeadlockRunning()
        {
            return Process.GetProcesses()
                .Any(process =>
                    process.ProcessName.Equals("deadlock", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName.Equals("deadlock_win64", StringComparison.OrdinalIgnoreCase));
        }

        private bool EnsureDlmmClosedBeforeGameWrite(string actionName)
        {
            var dlmmProcesses = GetDlmmProcesses();
            if (dlmmProcesses.Count == 0)
                return true;

            var confirmation = MessageBox.Show(
                $"{actionName} needs DLMM closed first.\n\nDLMM can keep state in memory or rebuild files while this app is applying changes, which can stop skins from changing.\n\nClose DLMM now?",
                "Close DLMM",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                SetNotice("DLMM still open", "Close DLMM, then try again so the app can apply changes cleanly.", NoticeKind.Warning, showBanner: true);
                return false;
            }

            var remaining = CloseDlmmProcesses(dlmmProcesses);
            if (remaining.Count == 0)
            {
                SetNotice("DLMM closed", "DLMM was closed before applying changes.", NoticeKind.Info, showBanner: true);
                return true;
            }

            var names = string.Join(", ", remaining.Select(process => process.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase));
            SetNotice("Could not close DLMM", $"DLMM is still running ({names}). Close it manually, then try again.", NoticeKind.Warning, showBanner: true);
            return false;
        }

        private static List<Process> GetDlmmProcesses()
        {
            return Process.GetProcesses()
                .Where(IsDlmmProcess)
                .ToList();
        }

        private static bool IsDlmmProcess(Process process)
        {
            var processName = process.ProcessName;
            var title = GetProcessTitle(process);
            var path = GetProcessPath(process);

            return ContainsDlmmIdentifier(processName)
                || ContainsDlmmIdentifier(title)
                || ContainsDlmmIdentifier(path);
        }

        private static bool ContainsDlmmIdentifier(string value)
        {
            return value.Contains("deadlock-mod-manager", StringComparison.OrdinalIgnoreCase)
                || value.Contains("deadlock mod manager", StringComparison.OrdinalIgnoreCase)
                || value.Contains("dev.stormix.deadlock-mod-manager", StringComparison.OrdinalIgnoreCase);
        }

        private static List<Process> CloseDlmmProcesses(List<Process> processes)
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    if (process.MainWindowHandle != IntPtr.Zero)
                        process.CloseMainWindow();
                }
                catch
                {
                    // Fall through to the force-close pass.
                }
            }

            WaitForProcessesToExit(processes, milliseconds: 4000);

            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Report anything that survives below.
                }
            }

            WaitForProcessesToExit(processes, milliseconds: 4000);

            return processes
                .Where(process =>
                {
                    try
                    {
                        return !process.HasExited;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();
        }

        private static void WaitForProcessesToExit(List<Process> processes, int milliseconds)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            foreach (var process in processes)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return;

                try
                {
                    if (!process.HasExited)
                        process.WaitForExit((int)remaining.TotalMilliseconds);
                }
                catch
                {
                    // Process may have exited between checks.
                }
            }
        }

        private static string GetProcessTitle(Process process)
        {
            try
            {
                return process.MainWindowTitle ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string GetProcessPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? "";
            }
            catch
            {
                return "";
            }
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

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInsideInteractiveInput(e.OriginalSource as DependencyObject))
                return;

            Keyboard.ClearFocus();
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

        private static bool IsInsideInteractiveInput(DependencyObject? source)
        {
            while (source is not null)
            {
                if (source is TextBox
                    or ComboBox
                    or ComboBoxItem
                    or ButtonBase
                    or ToggleButton)
                {
                    return true;
                }

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
                HeroDisplayService.ToDisplayName(mod.Hero)
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
            if (string.IsNullOrWhiteSpace(searchText))
                return "";

            var normalized = new StringBuilder(searchText.Length);
            foreach (var character in searchText)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (char.IsControl(character)
                    || category is UnicodeCategory.Format
                        or UnicodeCategory.PrivateUse
                        or UnicodeCategory.Surrogate
                        or UnicodeCategory.OtherNotAssigned)
                {
                    continue;
                }

                normalized.Append(character);
            }

            return normalized.ToString().Trim();
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
