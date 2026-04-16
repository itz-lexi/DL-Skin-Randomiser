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

        public static void Apply(List<DlmmMod> mods, UserPreferences preferences)
        {
            foreach (var mod in mods)
            {
                if (string.IsNullOrWhiteSpace(mod.RemoteId))
                    continue;

                if (!preferences.Mods.TryGetValue(mod.RemoteId, out var preference))
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

        public static void Save(string path, IEnumerable<DlmmMod> mods, string statePath)
        {
            var existingPreferences = Load(path);
            var preferences = new UserPreferences
            {
                StatePath = statePath,
                CustomFolders = existingPreferences.CustomFolders,
                LastSessionLoadout = existingPreferences.LastSessionLoadout
            };

            foreach (var mod in mods.Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId)))
            {
                var folder = mod.Folder.Trim().ToLowerInvariant();
                var includedInRandomizer = mod.IncludedInRandomizer && string.IsNullOrWhiteSpace(folder);

                preferences.Mods[mod.RemoteId] = new ModPreference
                {
                    RemoteId = mod.RemoteId,
                    Hero = mod.Hero.Trim().ToLowerInvariant(),
                    Folder = folder,
                    IncludedInRandomizer = includedInRandomizer
                };
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(preferences, JsonOptions));
        }

        public static void SaveLastSessionLoadout(string path, List<LoadoutPick> loadout)
        {
            var preferences = Load(path);
            preferences.LastSessionLoadout = loadout;
            SavePreferences(path, preferences);
        }

        public static void SaveStatePath(string path, string statePath)
        {
            var preferences = Load(path);
            preferences.StatePath = statePath;
            SavePreferences(path, preferences);
        }

        public static void AddCustomFolder(string path, string folderName)
        {
            var folder = HeroDisplayService.ToKey(folderName);
            if (string.IsNullOrWhiteSpace(folder) || folder == "unknown")
                return;

            var preferences = Load(path);
            if (!preferences.CustomFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                preferences.CustomFolders.Add(folder);

            preferences.CustomFolders = preferences.CustomFolders
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(folder => HeroDisplayService.ToDisplayName(folder))
                .ToList();

            SavePreferences(path, preferences);
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
