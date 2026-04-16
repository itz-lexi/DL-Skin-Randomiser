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

        public static DlmmStateSnapshot Load(string path)
        {
            var json = File.ReadAllText(path);
            using var outerDocument = JsonDocument.Parse(json);

            var stateElement = GetStateElement(outerDocument.RootElement);
            var activeProfileId = TryGetString(stateElement, "activeProfileId");
            var enabledMods = GetEnabledMods(stateElement, activeProfileId);

            var result = new DlmmStateSnapshot
            {
                Path = path,
                ActiveProfileId = activeProfileId,
                GamePath = TryGetString(stateElement, "gamePath")
            };

            var modElements = GetModElements(stateElement);
            if (modElements.Count == 0)
                return result;

            foreach (var modElement in modElements)
            {
                var remoteId = TryGetString(modElement, "remoteId");
                var name = TryGetString(modElement, "name");

                result.Mods.Add(new DlmmMod
                {
                    Id = TryGetString(modElement, "id"),
                    RemoteId = remoteId,
                    Name = name,
                    Status = TryGetString(modElement, "status"),
                    Category = TryGetString(modElement, "category"),
                    ImageUrl = TryGetFirstString(modElement, "images"),
                    Hero = DetectHero(modElement, name),
                    Enabled = IsEnabled(enabledMods, remoteId)
                });
            }

            Console.WriteLine($"Loaded {result.Mods.Count} mods");
            Console.WriteLine($"Enabled: {result.Mods.Count(mod => mod.Enabled)}");

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

        public static void SaveEnabledMods(string path, IReadOnlyCollection<DlmmMod> mods)
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
            var activeProfileNode = profilesNode[activeProfileId] as JsonObject
                ?? throw new InvalidOperationException($"DLMM active profile '{activeProfileId}' was not found.");

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
            else
            {
                outerNode["state"] = stateNode.DeepClone();
            }

            var backupPath = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backupPath, overwrite: false);
            File.WriteAllText(path, outerNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

        private static string DetectHero(JsonElement modElement, string name)
        {
            var nameHero = HeroDetector.Detect(name);

            return nameHero;
        }
    }
}
