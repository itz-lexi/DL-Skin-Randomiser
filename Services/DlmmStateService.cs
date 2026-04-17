using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class DlmmStateService
    {
        private const string LocalConfigPropertyName = "local-config";

        public static string DefaultStatePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "dev.stormix.deadlock-mod-manager",
                "state.json");

        public static DlmmStateSnapshot Load(string path, string selectedProfileId = "")
        {
            var json = File.ReadAllText(path);
            using var outerDocument = JsonDocument.Parse(json);

            var stateElement = GetStateElement(outerDocument.RootElement);
            var activeProfileId = TryGetString(stateElement, "activeProfileId");
            var profiles = GetProfiles(stateElement);
            var profileId = ResolveProfileId(activeProfileId, selectedProfileId, profiles);
            var enabledMods = GetEnabledMods(stateElement, profileId);
            var profileModElements = GetProfileModElements(stateElement, profileId);
            var localModsByRemoteId = GetLocalModElementsByRemoteId(stateElement);

            var result = new DlmmStateSnapshot
            {
                Path = path,
                ActiveProfileId = activeProfileId,
                SelectedProfileId = profileId,
                GamePath = TryGetString(stateElement, "gamePath"),
                Profiles = profiles
            };

            var modElements = profileModElements.Count > 0
                ? GetFreshProfileModElements(profileModElements, localModsByRemoteId)
                : GetModElements(stateElement);
            if (modElements.Count == 0)
                return result;

            var hasProfileModList = profileModElements.Count > 0;

            foreach (var modElement in modElements)
            {
                var remoteId = TryGetString(modElement, "remoteId");
                var name = TryGetString(modElement, "name");
                var isEnabledInProfile = IsEnabled(enabledMods, remoteId);

                var installedVpks = GetStringArray(modElement, "installedVpks");
                result.Mods.Add(new DlmmMod
                {
                    Id = TryGetString(modElement, "id"),
                    RemoteId = remoteId,
                    Name = name,
                    Status = TryGetString(modElement, "status"),
                    Category = TryGetString(modElement, "category"),
                    ImageUrl = TryGetFirstString(modElement, "images"),
                    InstalledVpks = installedVpks.ToList(),
                    DlmmInstalledVpks = installedVpks.ToList(),
                    Hero = DetectHero(modElement, name),
                    IsInSelectedProfile = hasProfileModList || IsInProfile(enabledMods, remoteId),
                    IsEnabledInDlmmProfile = isEnabledInProfile,
                    Enabled = isEnabledInProfile
                });
            }

            return result;
        }

        private static List<JsonElement> GetProfileModElements(JsonElement stateElement, string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return [];

            var profiles = TryGetProperty(stateElement, "profiles");
            if (profiles is not { ValueKind: JsonValueKind.Object })
                return [];

            if (!profiles.Value.TryGetProperty(profileId, out var profileElement))
                return [];

            if (!profileElement.TryGetProperty("mods", out var profileMods))
                return [];

            return profileMods.ValueKind switch
            {
                JsonValueKind.Array => profileMods
                    .EnumerateArray()
                    .Where(IsLikelyModElement)
                    .ToList(),
                JsonValueKind.Object => profileMods
                    .EnumerateObject()
                    .Select(property => property.Value)
                    .Where(IsLikelyModElement)
                    .ToList(),
                _ => []
            };
        }

        private static List<JsonElement> GetFreshProfileModElements(
            List<JsonElement> profileModElements,
            IReadOnlyDictionary<string, JsonElement> localModsByRemoteId)
        {
            var result = new List<JsonElement>();
            var seenRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var profileModElement in profileModElements)
            {
                var remoteId = TryGetString(profileModElement, "remoteId");
                if (!string.IsNullOrWhiteSpace(remoteId) && !seenRemoteIds.Add(remoteId))
                    continue;

                result.Add(!string.IsNullOrWhiteSpace(remoteId)
                    && localModsByRemoteId.TryGetValue(remoteId, out var localModElement)
                        ? localModElement
                        : profileModElement);
            }

            return result;
        }

        private static Dictionary<string, JsonElement> GetLocalModElementsByRemoteId(JsonElement stateElement)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var localMods = TryGetProperty(stateElement, "localMods");
            if (localMods is not { ValueKind: JsonValueKind.Array })
                return result;

            foreach (var modElement in localMods.Value.EnumerateArray().Where(IsLikelyModElement))
            {
                var remoteId = TryGetString(modElement, "remoteId");
                if (string.IsNullOrWhiteSpace(remoteId))
                    continue;

                if (!result.ContainsKey(remoteId) || IsInstalledMod(modElement))
                    result[remoteId] = modElement;
            }

            return result;
        }

        private static List<JsonElement> GetModElements(JsonElement stateElement)
        {
            var modsByRemoteId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var modsWithoutRemoteId = new List<JsonElement>();

            foreach (var modElement in EnumerateLikelyModElements(stateElement))
            {
                var remoteId = TryGetString(modElement, "remoteId");
                if (string.IsNullOrWhiteSpace(remoteId))
                {
                    modsWithoutRemoteId.Add(modElement);
                    continue;
                }

                if (!modsByRemoteId.ContainsKey(remoteId) || IsInstalledMod(modElement))
                    modsByRemoteId[remoteId] = modElement;
            }

            return modsByRemoteId.Values.Concat(modsWithoutRemoteId).ToList();
        }

        private static IEnumerable<JsonElement> EnumerateLikelyModElements(JsonElement stateElement)
        {
            var localMods = TryGetProperty(stateElement, "localMods");
            if (localMods is { ValueKind: JsonValueKind.Array })
            {
                foreach (var modElement in localMods.Value.EnumerateArray().Where(IsLikelyModElement))
                    yield return modElement;
            }

            foreach (var modElement in EnumerateNestedArrayItems(stateElement).Where(IsLikelyModElement))
                yield return modElement;
        }

        private static IEnumerable<JsonElement> EnumerateNestedArrayItems(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        foreach (var nestedElement in EnumerateNestedArrayItems(property.Value))
                            yield return nestedElement;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var arrayElement in element.EnumerateArray())
                    {
                        yield return arrayElement;

                        if (arrayElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            foreach (var nestedElement in EnumerateNestedArrayItems(arrayElement))
                                yield return nestedElement;
                        }
                    }
                    break;
            }
        }

        private static bool IsLikelyModElement(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return false;

            var remoteId = TryGetString(element, "remoteId");
            var name = TryGetString(element, "name");

            return !string.IsNullOrWhiteSpace(remoteId)
                && !string.IsNullOrWhiteSpace(name);
        }

        private static bool IsInstalledMod(JsonElement element)
        {
            return string.Equals(TryGetString(element, "status"), "installed", StringComparison.OrdinalIgnoreCase);
        }

        public static string SaveEnabledMods(string path, IReadOnlyCollection<DlmmMod> mods, string selectedProfileId)
        {
            var json = File.ReadAllText(path);
            var outerNode = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException("DLMM state root must be a JSON object.");

            var localConfigWasString = outerNode[LocalConfigPropertyName] is JsonValue;
            var configNode = GetConfigNode(outerNode);
            var stateNode = configNode["state"] as JsonObject
                ?? throw new InvalidOperationException("DLMM local-config is missing state.");

            var activeProfileId = stateNode["activeProfileId"]?.GetValue<string>() ?? "";
            var profilesNode = stateNode["profiles"] as JsonObject
                ?? throw new InvalidOperationException("DLMM state is missing profiles.");
            var profileId = !string.IsNullOrWhiteSpace(selectedProfileId) && profilesNode.ContainsKey(selectedProfileId)
                ? selectedProfileId
                : activeProfileId;
            var activeProfileNode = profilesNode[profileId] as JsonObject
                ?? throw new InvalidOperationException($"DLMM profile '{profileId}' was not found.");

            var enabledModsNode = activeProfileNode["enabledMods"] as JsonObject;
            if (enabledModsNode is null)
            {
                enabledModsNode = [];
                activeProfileNode["enabledMods"] = enabledModsNode;
            }

            foreach (var mod in mods.Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId)))
            {
                var entryNode = enabledModsNode[mod.RemoteId] as JsonObject;
                if (entryNode is null)
                {
                    entryNode = [];
                    enabledModsNode[mod.RemoteId] = entryNode;
                }

                entryNode["remoteId"] = mod.RemoteId;
                entryNode["enabled"] = mod.Enabled;
                entryNode["lastModified"] = DateTime.UtcNow.ToString("O");
            }

            if (localConfigWasString)
            {
                outerNode[LocalConfigPropertyName] = configNode.ToJsonString();
            }
            else if (outerNode.ContainsKey("state"))
            {
                outerNode["state"] = stateNode.DeepClone();
            }
            else
            {
                configNode["state"] = stateNode.DeepClone();
            }

            var backupPath = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backupPath, overwrite: false);
            File.WriteAllText(path, outerNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return backupPath;
        }

        public static string RemoveProfileMod(string path, string selectedProfileId, string remoteId)
        {
            if (string.IsNullOrWhiteSpace(remoteId))
                throw new InvalidOperationException("The selected mod does not have a DLMM remoteId.");

            var json = File.ReadAllText(path);
            var outerNode = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException("DLMM state root must be a JSON object.");

            var localConfigWasString = outerNode[LocalConfigPropertyName] is JsonValue;
            var configNode = GetConfigNode(outerNode);
            var stateNode = configNode["state"] as JsonObject
                ?? throw new InvalidOperationException("DLMM local-config is missing state.");

            var activeProfileId = stateNode["activeProfileId"]?.GetValue<string>() ?? "";
            var profilesNode = stateNode["profiles"] as JsonObject
                ?? throw new InvalidOperationException("DLMM state is missing profiles.");
            var profileId = !string.IsNullOrWhiteSpace(selectedProfileId) && profilesNode.ContainsKey(selectedProfileId)
                ? selectedProfileId
                : activeProfileId;
            var activeProfileNode = profilesNode[profileId] as JsonObject
                ?? throw new InvalidOperationException($"DLMM profile '{profileId}' was not found.");

            var removedFromEnabledMods = RemoveEnabledMod(activeProfileNode, remoteId);
            var removedFromProfileMods = RemoveProfileModEntry(activeProfileNode["mods"], remoteId);
            if (!removedFromEnabledMods && !removedFromProfileMods)
                throw new InvalidOperationException("That mod was not found in the selected DLMM profile.");

            if (localConfigWasString)
            {
                outerNode[LocalConfigPropertyName] = configNode.ToJsonString();
            }
            else if (outerNode.ContainsKey("state"))
            {
                outerNode["state"] = stateNode.DeepClone();
            }
            else
            {
                configNode["state"] = stateNode.DeepClone();
            }

            var backupPath = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backupPath, overwrite: false);
            File.WriteAllText(path, outerNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return backupPath;
        }

        private static bool RemoveEnabledMod(JsonObject profileNode, string remoteId)
        {
            if (profileNode["enabledMods"] is not JsonObject enabledModsNode)
                return false;

            return enabledModsNode.Remove(remoteId);
        }

        private static bool RemoveProfileModEntry(JsonNode? modsNode, string remoteId)
        {
            if (modsNode is JsonArray modsArray)
            {
                var removed = false;
                for (var index = modsArray.Count - 1; index >= 0; index--)
                {
                    if (!NodeRemoteIdEquals(modsArray[index], remoteId))
                        continue;

                    modsArray.RemoveAt(index);
                    removed = true;
                }

                return removed;
            }

            if (modsNode is JsonObject modsObject)
            {
                var keysToRemove = modsObject
                    .Where(property => string.Equals(property.Key, remoteId, StringComparison.OrdinalIgnoreCase)
                        || NodeRemoteIdEquals(property.Value, remoteId))
                    .Select(property => property.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                    modsObject.Remove(key);

                return keysToRemove.Count > 0;
            }

            return false;
        }

        private static bool NodeRemoteIdEquals(JsonNode? node, string remoteId)
        {
            if (node is not JsonObject modObject)
                return false;

            return string.Equals(TryGetString(modObject, "remoteId"), remoteId, StringComparison.OrdinalIgnoreCase);
        }

        private static JsonElement GetStateElement(JsonElement root)
        {
            if (root.TryGetProperty("state", out var directState))
                return directState;

            if (!root.TryGetProperty(LocalConfigPropertyName, out var localConfig))
                return default;

            if (localConfig.ValueKind == JsonValueKind.String)
            {
                using var innerDocument = JsonDocument.Parse(localConfig.GetString() ?? "{}");
                return innerDocument.RootElement.TryGetProperty("state", out var nestedState)
                    ? nestedState.Clone()
                    : default;
            }

            return localConfig.TryGetProperty("state", out var configState)
                ? configState
                : default;
        }

        private static JsonObject GetConfigNode(JsonObject outerNode)
        {
            if (outerNode[LocalConfigPropertyName] is JsonValue localConfigValue)
            {
                var localConfigJson = localConfigValue.GetValue<string>();
                return JsonNode.Parse(localConfigJson) as JsonObject
                    ?? throw new InvalidOperationException("DLMM local-config must be a JSON object.");
            }

            if (outerNode.ContainsKey("state"))
                return outerNode;

            return outerNode[LocalConfigPropertyName] as JsonObject
                ?? throw new InvalidOperationException("DLMM state is missing local-config.");
        }

        private static List<CharacterOption> GetProfiles(JsonElement stateElement)
        {
            var profiles = TryGetProperty(stateElement, "profiles");
            if (profiles is not { ValueKind: JsonValueKind.Object })
                return [];

            return profiles.Value
                .EnumerateObject()
                .Select(profile => new CharacterOption
                {
                    Key = profile.Name,
                    Name = GetProfileDisplayName(profile.Name, profile.Value)
                })
                .OrderBy(profile => profile.Name)
                .ToList();
        }

        private static string GetProfileDisplayName(string profileId, JsonElement profileElement)
        {
            var name = TryGetString(profileElement, "name");
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            var displayName = TryGetString(profileElement, "displayName");
            if (!string.IsNullOrWhiteSpace(displayName))
                return displayName;

            var title = TryGetString(profileElement, "title");
            if (!string.IsNullOrWhiteSpace(title))
                return title;

            return profileId;
        }

        private static string ResolveProfileId(string activeProfileId, string selectedProfileId, List<CharacterOption> profiles)
        {
            if (!string.IsNullOrWhiteSpace(selectedProfileId)
                && profiles.Any(profile => string.Equals(profile.Key, selectedProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                return selectedProfileId;
            }

            if (!string.IsNullOrWhiteSpace(activeProfileId)
                && profiles.Any(profile => string.Equals(profile.Key, activeProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                return activeProfileId;
            }

            return profiles.FirstOrDefault()?.Key ?? "";
        }

        private static JsonElement? GetEnabledMods(JsonElement stateElement, string activeProfileId)
        {
            if (string.IsNullOrWhiteSpace(activeProfileId))
                return null;

            var profiles = TryGetProperty(stateElement, "profiles");
            if (profiles is null)
                return null;

            if (!profiles.Value.TryGetProperty(activeProfileId, out var activeProfile))
                return null;

            return activeProfile.TryGetProperty("enabledMods", out var enabledMods)
                ? enabledMods
                : null;
        }

        private static bool IsEnabled(JsonElement? enabledMods, string remoteId)
        {
            if (enabledMods is not { ValueKind: JsonValueKind.Object })
                return false;

            if (string.IsNullOrWhiteSpace(remoteId))
                return false;

            if (!enabledMods.Value.TryGetProperty(remoteId, out var enabledEntry))
                return false;

            return enabledEntry.TryGetProperty("enabled", out var enabledValue)
                && enabledValue.ValueKind == JsonValueKind.True;
        }

        private static bool IsInProfile(JsonElement? enabledMods, string remoteId)
        {
            if (enabledMods is not { ValueKind: JsonValueKind.Object })
                return false;

            if (string.IsNullOrWhiteSpace(remoteId))
                return false;

            return enabledMods.Value.TryGetProperty(remoteId, out _);
        }

        private static JsonElement? TryGetProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            return element.TryGetProperty(propertyName, out var property)
                ? property
                : null;
        }

        private static string TryGetString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return "";

            if (!element.TryGetProperty(propertyName, out var property))
                return "";

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString() ?? "",
                JsonValueKind.Number => property.GetRawText(),
                _ => ""
            };
        }

        private static string TryGetString(JsonObject node, string propertyName)
        {
            var value = node[propertyName];
            if (value is null)
                return "";

            return value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>() ?? "",
                JsonValueKind.Number => value.ToJsonString(),
                _ => ""
            };
        }

        private static string TryGetFirstString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return "";

            if (!element.TryGetProperty(propertyName, out var property))
                return "";

            if (property.ValueKind != JsonValueKind.Array)
                return "";

            var first = property.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.String
                ? first.GetString() ?? ""
                : "";
        }

        private static List<string> GetStringArray(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return [];

            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
                return [];

            return property
                .EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string DetectHero(JsonElement modElement, string name)
        {
            var nameHero = HeroDetector.Detect(name);

            return nameHero;
        }
    }
}
