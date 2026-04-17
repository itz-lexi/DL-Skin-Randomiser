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
            var customFolderLookup = profilePreferences.CustomFolders
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .GroupBy(HeroDisplayService.ToKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

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
                if (customFolderLookup.TryGetValue(mod.Folder, out var displayFolder))
                    mod.Folder = displayFolder;

                mod.IncludedInRandomizer = preference.IncludedInRandomizer;
                if (!string.IsNullOrWhiteSpace(mod.Folder)
                    || string.Equals(mod.Hero, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    mod.IncludedInRandomizer = false;
                }
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
                profilePreferences.Mods[mod.RemoteId] = BuildModPreference(mod);
            }

            SavePreferences(path, existingPreferences);
        }

        public static void SaveMod(string path, DlmmMod mod, string statePath, string profileId)
        {
            if (string.IsNullOrWhiteSpace(mod.RemoteId))
                return;

            var existingPreferences = Load(path);
            existingPreferences.StatePath = statePath;
            existingPreferences.SelectedProfileId = profileId;

            var profilePreferences = GetOrCreateProfilePreferences(existingPreferences, profileId);
            profilePreferences.Mods[mod.RemoteId] = BuildModPreference(mod);

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
            var displayFolder = folderName.Trim();
            var folderKey = HeroDisplayService.ToKey(displayFolder);
            if (string.IsNullOrWhiteSpace(folderKey) || folderKey == "unknown")
                return;

            var preferences = Load(path);
            var customFolders = GetCustomFolders(preferences, profileId);
            var existingIndex = customFolders.FindIndex(folder => string.Equals(HeroDisplayService.ToKey(folder), folderKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                customFolders[existingIndex] = displayFolder;
            else
                customFolders.Add(displayFolder);

            customFolders = customFolders
                .GroupBy(HeroDisplayService.ToKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(folder => folder)
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
            var folderKey = HeroDisplayService.ToKey(folderName);
            if (string.IsNullOrWhiteSpace(folderKey))
                return;

            var preferences = Load(path);
            var customFolders = GetCustomFolders(preferences, profileId)
                .Where(existingFolder => !string.Equals(HeroDisplayService.ToKey(existingFolder), folderKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            SetCustomFolders(preferences, profileId, customFolders);
            SavePreferences(path, preferences);
        }

        public static void RenameCustomFolder(string path, string profileId, string oldFolderName, string newFolderName)
        {
            var oldFolderKey = HeroDisplayService.ToKey(oldFolderName);
            var newDisplayFolder = newFolderName.Trim();
            var newFolderKey = HeroDisplayService.ToKey(newDisplayFolder);

            if (string.IsNullOrWhiteSpace(oldFolderKey)
                || string.IsNullOrWhiteSpace(newFolderKey)
                || newFolderKey == "unknown")
            {
                return;
            }

            var preferences = Load(path);
            var customFolders = GetCustomFolders(preferences, profileId)
                .Where(existingFolder => !string.Equals(HeroDisplayService.ToKey(existingFolder), oldFolderKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var existingIndex = customFolders.FindIndex(existingFolder => string.Equals(HeroDisplayService.ToKey(existingFolder), newFolderKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                customFolders[existingIndex] = newDisplayFolder;
            else
                customFolders.Add(newDisplayFolder);

            customFolders = customFolders
                .GroupBy(HeroDisplayService.ToKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(folder => folder)
                .ToList();

            var profilePreferences = GetOrCreateProfilePreferences(preferences, profileId);
            foreach (var modPreference in profilePreferences.Mods.Values)
            {
                if (string.Equals(HeroDisplayService.ToKey(modPreference.Folder), oldFolderKey, StringComparison.OrdinalIgnoreCase))
                    modPreference.Folder = newFolderKey;
            }

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

        private static ModPreference BuildModPreference(DlmmMod mod)
        {
            var folder = mod.Folder.Trim().ToLowerInvariant();
            var hero = mod.Hero.Trim().ToLowerInvariant();
            var includedInRandomizer = mod.IncludedInRandomizer
                && string.IsNullOrWhiteSpace(folder)
                && !string.Equals(hero, "unknown", StringComparison.OrdinalIgnoreCase);

            return new ModPreference
            {
                RemoteId = mod.RemoteId,
                Hero = hero,
                Folder = folder,
                IncludedInRandomizer = includedInRandomizer
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
