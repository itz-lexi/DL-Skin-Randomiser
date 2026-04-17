using System.IO;
using System.Text.Json;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class UserPreferenceService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static string DefaultPreferencesPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DL-Skin-Randomiser",
                "preferences.json");

        public static UserPreferences Load(string path)
        {
            if (!File.Exists(path))
                return new UserPreferences();

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new UserPreferences();

            return JsonSerializer.Deserialize<UserPreferences>(json, JsonOptions) ?? new UserPreferences();
        }

        public static void Apply(List<DlmmMod> mods, UserPreferences preferences, string profileId)
        {
            var profilePreferences = GetProfilePreferences(preferences, profileId, useLegacyFallback: true);

            foreach (var mod in mods)
            {
                if (string.IsNullOrWhiteSpace(mod.RemoteId))
                    continue;

                if (!profilePreferences.Mods.TryGetValue(mod.RemoteId, out var preference))
                    continue;

                var preferredHero = preference.Hero.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(preferredHero) && (preferredHero != "unknown" || mod.Hero == "unknown"))
                    mod.Hero = preferredHero;

                mod.Folder = preference.Folder.Trim().ToLowerInvariant();
                mod.IncludedInRandomizer = preference.IncludedInRandomizer;
                if (!string.IsNullOrWhiteSpace(mod.Folder))
                    mod.IncludedInRandomizer = false;
            }
        }

        public static void Save(string path, IEnumerable<DlmmMod> mods, string statePath, string profileId)
        {
            var existingPreferences = Load(path);
            existingPreferences.StatePath = statePath;
            existingPreferences.SelectedProfileId = profileId;

            var profilePreferences = GetOrCreateProfilePreferences(existingPreferences, profileId);
            profilePreferences.Mods = [];

            foreach (var mod in mods.Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId)))
            {
                var folder = mod.Folder.Trim().ToLowerInvariant();
                var includedInRandomizer = mod.IncludedInRandomizer && string.IsNullOrWhiteSpace(folder);

                profilePreferences.Mods[mod.RemoteId] = new ModPreference
                {
                    RemoteId = mod.RemoteId,
                    Hero = mod.Hero.Trim().ToLowerInvariant(),
                    Folder = folder,
                    IncludedInRandomizer = includedInRandomizer
                };
            }

            SavePreferences(path, existingPreferences);
        }

        public static void SaveLastSessionLoadout(string path, string profileId, List<LoadoutPick> loadout)
        {
            var preferences = Load(path);
            var profilePreferences = GetOrCreateProfilePreferences(preferences, profileId);
            profilePreferences.LastSessionLoadout = loadout;
            SavePreferences(path, preferences);
        }

        public static void SaveStatePath(string path, string statePath)
        {
            var preferences = Load(path);
            preferences.StatePath = statePath;
            SavePreferences(path, preferences);
        }

        public static void SaveSelectedProfile(string path, string profileId)
        {
            var preferences = Load(path);
            PreserveLegacyPreferencesForPreviousProfile(preferences);
            preferences.SelectedProfileId = profileId;
            SavePreferences(path, preferences);
        }

        public static void AddCustomFolder(string path, string folderName)
        {
            AddCustomFolder(path, "", folderName);
        }

        public static void AddCustomFolder(string path, string profileId, string folderName)
        {
            var folder = HeroDisplayService.ToKey(folderName);
            if (string.IsNullOrWhiteSpace(folder) || folder == "unknown")
                return;

            var preferences = Load(path);
            var customFolders = GetCustomFolders(preferences, profileId);
            if (!customFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                customFolders.Add(folder);

            customFolders = customFolders
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(folder => HeroDisplayService.ToDisplayName(folder))
                .ToList();

            SetCustomFolders(preferences, profileId, customFolders);

            SavePreferences(path, preferences);
        }

        public static void RemoveCustomFolder(string path, string folderName)
        {
            RemoveCustomFolder(path, "", folderName);
        }

        public static void RemoveCustomFolder(string path, string profileId, string folderName)
        {
            var folder = HeroDisplayService.ToKey(folderName);
            if (string.IsNullOrWhiteSpace(folder))
                return;

            var preferences = Load(path);
            var customFolders = GetCustomFolders(preferences, profileId)
                .Where(existingFolder => !string.Equals(existingFolder, folder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            SetCustomFolders(preferences, profileId, customFolders);
            SavePreferences(path, preferences);
        }

        public static ProfilePreferences GetProfilePreferences(UserPreferences preferences, string profileId, bool useLegacyFallback = false)
        {
            if (!string.IsNullOrWhiteSpace(profileId)
                && preferences.Profiles.TryGetValue(profileId, out var profilePreferences))
            {
                return profilePreferences;
            }

            if (useLegacyFallback
                && preferences.Profiles.Count == 0
                && string.Equals(preferences.SelectedProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                return new ProfilePreferences
                {
                    CustomFolders = preferences.CustomFolders,
                    LastSessionLoadout = preferences.LastSessionLoadout,
                    Mods = preferences.Mods
                };
            }

            return new ProfilePreferences();
        }

        private static ProfilePreferences GetOrCreateProfilePreferences(UserPreferences preferences, string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                profileId = preferences.SelectedProfileId;

            if (string.IsNullOrWhiteSpace(profileId))
                profileId = "default";

            if (!preferences.Profiles.TryGetValue(profileId, out var profilePreferences))
            {
                profilePreferences = new ProfilePreferences();
                preferences.Profiles[profileId] = profilePreferences;
            }

            return profilePreferences;
        }

        private static List<string> GetCustomFolders(UserPreferences preferences, string profileId)
        {
            return string.IsNullOrWhiteSpace(profileId)
                ? preferences.CustomFolders
                : GetOrCreateProfilePreferences(preferences, profileId).CustomFolders;
        }

        private static void SetCustomFolders(UserPreferences preferences, string profileId, List<string> customFolders)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                preferences.CustomFolders = customFolders;
                return;
            }

            GetOrCreateProfilePreferences(preferences, profileId).CustomFolders = customFolders;
        }

        private static void PreserveLegacyPreferencesForPreviousProfile(UserPreferences preferences)
        {
            if (preferences.Profiles.Count > 0 || string.IsNullOrWhiteSpace(preferences.SelectedProfileId))
                return;

            if (preferences.CustomFolders.Count == 0
                && preferences.LastSessionLoadout.Count == 0
                && preferences.Mods.Count == 0)
            {
                return;
            }

            preferences.Profiles[preferences.SelectedProfileId] = new ProfilePreferences
            {
                CustomFolders = preferences.CustomFolders,
                LastSessionLoadout = preferences.LastSessionLoadout,
                Mods = preferences.Mods
            };
        }

        private static void SavePreferences(string path, UserPreferences preferences)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(preferences, JsonOptions));
        }
    }
}
