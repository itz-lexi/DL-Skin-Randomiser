using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class ManifestGameModStagingService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static string ManifestPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DL-Skin-Randomiser",
                "staging-manifest.json");

        public static ApplyResult Stage(string gamePath, IReadOnlyCollection<DlmmMod> mods)
        {
            var result = new ApplyResult
            {
                RequiresDlmmApply = false
            };

            var addonsPath = GetAddonsPath(gamePath);
            if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
            {
                result.RequiresDlmmApply = true;
                result.StagingSkippedCount = mods.Count;
                return result;
            }

            var manifest = LoadManifest();
            var activeManifestEntries = manifest.Entries
                .Where(entry => string.Equals(entry.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var ownedHeroKeys = mods
                .Where(IsRandomizerOwnedMod)
                .Select(mod => mod.Hero)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var controlledRemoteIds = mods
                .Where(mod => ownedHeroKeys.Contains(mod.Hero))
                .Where(mod => string.IsNullOrWhiteSpace(mod.Folder))
                .Where(mod => !string.Equals(mod.Hero, "unknown", StringComparison.OrdinalIgnoreCase))
                .Select(mod => mod.RemoteId)
                .Where(remoteId => !string.IsNullOrWhiteSpace(remoteId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sourceSelections = mods
                .Where(IsRandomizerOwnedMod)
                .Select(mod => BuildSourceSelection(addonsPath, mod))
                .Where(selection => selection.Sources.Count > 0)
                .ToList();
            result.StaleSourceVpkSkippedCount = sourceSelections.Sum(selection => selection.StaleSourceCount);
            var sourceSelectionsByRemoteId = sourceSelections
                .GroupBy(selection => selection.Mod.RemoteId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var ownedMods = sourceSelections
                .Select(selection => selection.Mod)
                .ToList();
            var enabledOwnedMods = ownedMods
                .Where(mod => mod.Enabled)
                .ToList();
            var desiredRemoteIds = enabledOwnedMods
                .Select(mod => mod.RemoteId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var desiredSources = enabledOwnedMods
                .Where(mod => sourceSelectionsByRemoteId.ContainsKey(mod.RemoteId))
                .SelectMany(mod => sourceSelectionsByRemoteId[mod.RemoteId].Sources.Select(source => new DesiredSource(mod, source)))
                .ToList();
            var desiredKeys = desiredSources
                .Select(source => BuildSourceKey(source.Mod.RemoteId, source.Source.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in activeManifestEntries)
            {
                if (!controlledRemoteIds.Contains(entry.RemoteId))
                    continue;

                var entryKey = BuildSourceKey(entry.RemoteId, entry.SourceFileName);
                if (desiredKeys.Contains(entryKey) && desiredRemoteIds.Contains(entry.RemoteId))
                    continue;

                if (TryDeleteOwnedLiveFile(addonsPath, entry))
                {
                    result.StagedDisabledCount++;
                    manifest.Entries.Remove(entry);
                }
                else
                {
                    result.StagingSkippedCount++;
                }
            }

            var usedLiveSlots = Directory
                .EnumerateFiles(addonsPath, "*.vpk", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !IsRemotePrefixed(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var source in desiredSources)
            {
                var existingEntry = manifest.Entries.FirstOrDefault(entry =>
                    string.Equals(entry.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.RemoteId, source.Mod.RemoteId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.SourceFileName, source.Source.Name, StringComparison.OrdinalIgnoreCase));
                var targetSlot = existingEntry?.LiveSlot;
                if (string.IsNullOrWhiteSpace(targetSlot))
                    targetSlot = ChooseLiveSlot(source.Source.Name, usedLiveSlots);

                if (string.IsNullOrWhiteSpace(targetSlot))
                {
                    result.StagingSkippedCount++;
                    continue;
                }

                var targetPath = Path.Combine(addonsPath, targetSlot);
                if (File.Exists(targetPath) && !CanReplaceLiveFile(targetPath, existingEntry))
                {
                    result.StagingSkippedCount++;
                    continue;
                }

                var sourceHash = TryGetFileHash(source.Source.FullName);
                if (string.IsNullOrWhiteSpace(sourceHash))
                {
                    result.StagingSkippedCount++;
                    continue;
                }

                File.Copy(source.Source.FullName, targetPath, overwrite: true);
                var stagedHash = TryGetFileHash(targetPath);
                if (string.IsNullOrWhiteSpace(stagedHash))
                {
                    result.StagingSkippedCount++;
                    continue;
                }

                if (existingEntry is null)
                {
                    manifest.Entries.Add(new StagingManifestEntry
                    {
                        GamePath = gamePath,
                        RemoteId = source.Mod.RemoteId,
                        ModName = source.Mod.Name,
                        SourcePath = source.Source.FullName,
                        SourceFileName = source.Source.Name,
                        SourceHash = sourceHash,
                        LiveSlot = targetSlot,
                        StagedHash = stagedHash,
                        StagedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    existingEntry.ModName = source.Mod.Name;
                    existingEntry.SourcePath = source.Source.FullName;
                    existingEntry.SourceHash = sourceHash;
                    existingEntry.LiveSlot = targetSlot;
                    existingEntry.StagedHash = stagedHash;
                    existingEntry.StagedAt = DateTime.UtcNow;
                }

                usedLiveSlots.Add(targetSlot);
                result.StagedEnabledCount++;
            }

            SaveManifest(manifest);
            result.GameFilesStaged = result.StagedEnabledCount > 0 || result.StagedDisabledCount > 0;
            result.AddonsBackupPath = ManifestPath;
            return result;
        }

        public static IReadOnlyDictionary<string, AppStagedLiveVpk> GetAppStagedLiveVpks(string gamePath)
        {
            var addonsPath = GetAddonsPath(gamePath);
            if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
                return new Dictionary<string, AppStagedLiveVpk>(StringComparer.OrdinalIgnoreCase);

            var manifest = LoadManifest();
            return manifest.Entries
                .Where(entry => string.Equals(entry.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
                .Where(entry =>
                {
                    var livePath = Path.Combine(addonsPath, Path.GetFileName(entry.LiveSlot));
                    return File.Exists(livePath)
                        && string.Equals(TryGetFileHash(livePath), entry.StagedHash, StringComparison.OrdinalIgnoreCase);
                })
                .GroupBy(entry => entry.LiveSlot, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var entry = group.OrderByDescending(entry => entry.StagedAt).First();
                        return new AppStagedLiveVpk(entry.RemoteId, entry.ModName, entry.LiveSlot, entry.SourceFileName, entry.StagedHash);
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsRandomizerOwnedMod(DlmmMod mod)
        {
            return mod.IncludedInRandomizer
                && !string.IsNullOrWhiteSpace(mod.RemoteId)
                && string.IsNullOrWhiteSpace(mod.Folder)
                && !string.Equals(mod.Hero, "unknown", StringComparison.OrdinalIgnoreCase);
        }

        private static SourceSelection BuildSourceSelection(string addonsPath, DlmmMod mod)
        {
            var allRemoteSources = FindAllRemoteSourceVpks(addonsPath, mod).ToList();
            if (allRemoteSources.Count == 0)
                return new SourceSelection(mod, [], 0);

            var dlmmSourceNames = BuildDlmmSourceNameSet(mod);
            var currentSources = dlmmSourceNames.Count == 0
                ? allRemoteSources
                : allRemoteSources
                    .Where(file => dlmmSourceNames.Contains(file.Name) || dlmmSourceNames.Contains(StripRemotePrefix(file.Name)))
                    .ToList();

            var sourceVpks = currentSources.Count > 0
                ? currentSources
                : allRemoteSources;
            var staleSourceCount = currentSources.Count > 0
                ? allRemoteSources.Count - currentSources.Count
                : 0;
            var selectedSources = sourceVpks
                .GroupBy(file => StripRemotePrefix(file.Name), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ThenByDescending(file => file.Length)
                    .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .First())
                .OrderBy(file => file.Name)
                .ToList();

            return new SourceSelection(mod, selectedSources, staleSourceCount + sourceVpks.Count - selectedSources.Count);
        }

        private static IEnumerable<FileInfo> FindAllRemoteSourceVpks(string addonsPath, DlmmMod mod)
        {
            if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
                return [];

            return Directory
                .EnumerateFiles(addonsPath, $"{mod.RemoteId}_*.vpk", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.Name)
                .ToList();
        }

        private static HashSet<string> BuildDlmmSourceNameSet(DlmmMod mod)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var installedVpk in mod.DlmmInstalledVpks.Select(Path.GetFileName))
            {
                if (string.IsNullOrWhiteSpace(installedVpk))
                    continue;

                names.Add(installedVpk);
                names.Add($"{mod.RemoteId}_{installedVpk}");
            }

            return names;
        }

        private static bool TryDeleteOwnedLiveFile(string addonsPath, StagingManifestEntry entry)
        {
            var liveSlot = Path.GetFileName(entry.LiveSlot);
            if (string.IsNullOrWhiteSpace(liveSlot))
                return false;

            var livePath = Path.Combine(addonsPath, liveSlot);
            if (!File.Exists(livePath))
                return true;

            var currentHash = TryGetFileHash(livePath);
            if (!string.Equals(currentHash, entry.StagedHash, StringComparison.OrdinalIgnoreCase))
                return false;

            File.Delete(livePath);
            return true;
        }

        private static bool CanReplaceLiveFile(string targetPath, StagingManifestEntry? existingEntry)
        {
            if (!File.Exists(targetPath))
                return true;

            if (existingEntry is null)
                return false;

            var currentHash = TryGetFileHash(targetPath);
            return string.Equals(currentHash, existingEntry.StagedHash, StringComparison.OrdinalIgnoreCase);
        }

        private static string ChooseLiveSlot(string sourceFileName, HashSet<string> usedLiveSlots)
        {
            var suffix = StripRemotePrefix(sourceFileName);
            if (IsLoadableLiveVpkName(suffix) && !usedLiveSlots.Contains(suffix))
                return suffix;

            for (var index = 1; index <= 999; index++)
            {
                var candidate = $"pak{index:D2}_dir.vpk";
                if (!usedLiveSlots.Contains(candidate))
                    return candidate;
            }

            return "";
        }

        private static string StripRemotePrefix(string fileName)
        {
            var underscoreIndex = fileName.IndexOf('_');
            return underscoreIndex >= 0 && underscoreIndex + 1 < fileName.Length
                ? fileName[(underscoreIndex + 1)..]
                : fileName;
        }

        private static bool IsLoadableLiveVpkName(string fileName)
        {
            return fileName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)
                && !IsRemotePrefixed(fileName);
        }

        private static bool IsRemotePrefixed(string fileName)
        {
            var underscoreIndex = fileName.IndexOf('_');
            return underscoreIndex > 0
                && fileName[..underscoreIndex].All(char.IsDigit);
        }

        private static string GetAddonsPath(string gamePath)
        {
            return string.IsNullOrWhiteSpace(gamePath)
                ? ""
                : Path.Combine(gamePath, "game", "citadel", "addons");
        }

        private static string BuildSourceKey(string remoteId, string sourceFileName)
        {
            return $"{remoteId}|{sourceFileName}";
        }

        private static string TryGetFileHash(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (IOException)
            {
                return "";
            }
            catch (UnauthorizedAccessException)
            {
                return "";
            }
        }

        private static StagingManifest LoadManifest()
        {
            if (!File.Exists(ManifestPath))
                return new StagingManifest();

            try
            {
                var json = File.ReadAllText(ManifestPath);
                return JsonSerializer.Deserialize<StagingManifest>(json, JsonOptions) ?? new StagingManifest();
            }
            catch (JsonException)
            {
                return new StagingManifest();
            }
            catch (IOException)
            {
                return new StagingManifest();
            }
        }

        private static void SaveManifest(StagingManifest manifest)
        {
            var directory = Path.GetDirectoryName(ManifestPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        }

        private sealed record DesiredSource(DlmmMod Mod, FileInfo Source);

        private sealed record SourceSelection(DlmmMod Mod, List<FileInfo> Sources, int StaleSourceCount);

        private sealed class StagingManifest
        {
            public List<StagingManifestEntry> Entries { get; set; } = [];
        }

        private sealed class StagingManifestEntry
        {
            public string GamePath { get; set; } = "";
            public string RemoteId { get; set; } = "";
            public string ModName { get; set; } = "";
            public string SourcePath { get; set; } = "";
            public string SourceFileName { get; set; } = "";
            public string SourceHash { get; set; } = "";
            public string LiveSlot { get; set; } = "";
            public string StagedHash { get; set; } = "";
            public DateTime StagedAt { get; set; }
        }
    }

    public sealed record AppStagedLiveVpk(
        string RemoteId,
        string ModName,
        string LiveSlot,
        string SourceFileName,
        string StagedHash);
}
