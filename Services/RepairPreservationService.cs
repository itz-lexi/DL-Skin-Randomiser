using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class RepairPreservationService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static string RepairBackupRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DL-Skin-Randomiser",
                "Backups",
                "Repair");

        public static RepairPreservationResult Preserve(string statePath, string gamePath)
        {
            var result = new RepairPreservationResult
            {
                BackupDirectory = Path.Combine(RepairBackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"))
            };
            Directory.CreateDirectory(result.BackupDirectory);

            PreserveDlmmLaunchSettings(statePath, result);
            PreserveGameInfoFiles(gamePath, result);
            WriteManifest(result);
            PruneOldRepairBackups();

            return result;
        }

        private static void PreserveDlmmLaunchSettings(string statePath, RepairPreservationResult result)
        {
            if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath))
                return;

            var root = JsonNode.Parse(File.ReadAllText(statePath));
            if (root is null)
                return;

            var matches = new JsonArray();
            CollectLaunchSettings(root, "$", matches);

            result.DlmmLaunchSettingCount = matches.Count;
            result.DlmmLaunchSettingsPath = Path.Combine(result.BackupDirectory, "dlmm-launch-settings.json");
            File.WriteAllText(
                result.DlmmLaunchSettingsPath,
                new JsonObject
                {
                    ["sourceStatePath"] = statePath,
                    ["capturedAt"] = DateTime.Now.ToString("O"),
                    ["matches"] = matches
                }.ToJsonString(JsonOptions));
        }

        private static void PreserveGameInfoFiles(string gamePath, RepairPreservationResult result)
        {
            foreach (var gameInfoPath in FindGameInfoFiles(gamePath))
            {
                var relativePath = Path.GetRelativePath(gamePath, gameInfoPath);
                var backupPath = Path.Combine(result.BackupDirectory, "gameinfo", relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? result.BackupDirectory);
                File.Copy(gameInfoPath, backupPath, overwrite: false);
                result.GameInfoBackupPaths.Add(backupPath);
            }

            result.GameInfoBackupCount = result.GameInfoBackupPaths.Count;
        }

        private static IEnumerable<string> FindGameInfoFiles(string gamePath)
        {
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                return [];

            var candidates = new[]
                {
                    Path.Combine(gamePath, "game", "citadel", "gameinfo.gi"),
                    Path.Combine(gamePath, "game", "citadel", "gameinfo.txt"),
                    Path.Combine(gamePath, "game", "citadel", "gameinfo_branchspecific.gi"),
                    Path.Combine(gamePath, "game", "core", "gameinfo.gi")
                }
                .Where(File.Exists);

            return candidates
                .Concat(Directory.EnumerateFiles(gamePath, "gameinfo.gi", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void WriteManifest(RepairPreservationResult result)
        {
            var gameInfoFiles = new JsonArray();
            foreach (var backupPath in result.GameInfoBackupPaths)
            {
                gameInfoFiles.Add(new JsonObject
                {
                    ["backupPath"] = backupPath,
                    ["sha256"] = TryGetFileHash(backupPath)
                });
            }

            File.WriteAllText(
                Path.Combine(result.BackupDirectory, "repair-preservation.json"),
                new JsonObject
                {
                    ["capturedAt"] = DateTime.Now.ToString("O"),
                    ["dlmmLaunchSettingsPath"] = result.DlmmLaunchSettingsPath,
                    ["dlmmLaunchSettingCount"] = result.DlmmLaunchSettingCount,
                    ["gameInfoBackupCount"] = result.GameInfoBackupCount,
                    ["gameInfoBackups"] = gameInfoFiles
                }.ToJsonString(JsonOptions));
        }

        private static void CollectLaunchSettings(JsonNode? node, string path, JsonArray matches)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        var propertyPath = $"{path}.{property.Key}";
                        if (IsLaunchSettingName(property.Key))
                        {
                            matches.Add(new JsonObject
                            {
                                ["path"] = propertyPath,
                                ["value"] = property.Value?.DeepClone()
                            });
                        }

                        CollectLaunchSettings(property.Value, propertyPath, matches);
                    }
                    break;
                case JsonArray array:
                    for (var index = 0; index < array.Count; index++)
                        CollectLaunchSettings(array[index], $"{path}[{index}]", matches);
                    break;
                case JsonValue value:
                    if (!value.TryGetValue<string>(out var text) || !LooksLikeJson(text))
                        break;

                    try
                    {
                        var nestedNode = JsonNode.Parse(text);
                        CollectLaunchSettings(nestedNode, $"{path}.$json", matches);
                    }
                    catch (JsonException)
                    {
                        // Some strings look JSON-ish but are plain launch args; keep scanning resilient.
                    }

                    break;
            }
        }

        private static bool LooksLikeJson(string value)
        {
            var trimmed = value.TrimStart();
            return trimmed.StartsWith('{') || trimmed.StartsWith('[');
        }

        private static bool IsLaunchSettingName(string name)
        {
            return ContainsPhrase(name, "launch")
                || ContainsPhrase(name, "argument")
                || ContainsPhrase(name, "parameter")
                || ContainsPhrase(name, "command")
                || ContainsPhrase(name, "steam");
        }

        private static bool ContainsPhrase(string value, string phrase)
        {
            return value.Contains(phrase, StringComparison.OrdinalIgnoreCase);
        }

        private static string TryGetFileHash(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch
            {
                return "";
            }
        }

        private static void PruneOldRepairBackups()
        {
            if (!Directory.Exists(RepairBackupRoot))
                return;

            foreach (var oldBackup in Directory.GetDirectories(RepairBackupRoot)
                         .Select(path => new DirectoryInfo(path))
                         .OrderByDescending(directory => directory.LastWriteTimeUtc)
                         .Skip(10))
            {
                try
                {
                    oldBackup.Delete(recursive: true);
                }
                catch
                {
                    // Repair backups should not block the actual repair flow.
                }
            }
        }
    }
}
