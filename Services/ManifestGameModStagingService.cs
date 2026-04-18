using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class ManifestGameModStagingService
    {
        private const string QuarantinedSourceSuffix = ".dlskin-source";
        private static string SourceVaultRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DL-Skin-Randomiser",
                "SourceVpks");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static string ManifestPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DL-Skin-Randomiser",
                "staging-manifest.json");

        public static ApplyResult Stage(
            string gamePath,
            IReadOnlyCollection<DlmmMod> mods,
            IReadOnlyCollection<DlmmMod>? sourceVaultMods = null)
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

            var stagingScopeMods = sourceVaultMods ?? mods;
            VaultRandomizerSourceVpks(gamePath, addonsPath, stagingScopeMods);
            var protectedLiveSlots = BuildDlmmManagedLiveSlots(gamePath, addonsPath, stagingScopeMods);

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
                .Select(mod => BuildSourceSelection(gamePath, addonsPath, mod))
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
            ClearKnownRandomizerLiveSlots(addonsPath, ownedMods, result, protectedLiveSlots);
            var stageableRemoteIds = sourceSelectionsByRemoteId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.EnabledModsWithoutStagedFiles = enabledOwnedMods
                .Where(mod => !stageableRemoteIds.Contains(mod.RemoteId))
                .Select(mod => mod.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.EnabledModsWithoutStagedFilesCount = result.EnabledModsWithoutStagedFiles.Count;

            var desiredSources = enabledOwnedMods
                .Where(mod => sourceSelectionsByRemoteId.ContainsKey(mod.RemoteId))
                .SelectMany(mod => sourceSelectionsByRemoteId[mod.RemoteId].Sources.Select(source => new DesiredSource(mod, source)))
                .ToList();
            var desiredKeys = desiredSources
                .Select(source => BuildSourceKey(source.Mod.RemoteId, GetSourceVpkName(source.Source)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in activeManifestEntries)
            {
                if (!controlledRemoteIds.Contains(entry.RemoteId))
                    continue;

                var liveSlot = Path.GetFileName(entry.LiveSlot);
                if (!string.IsNullOrWhiteSpace(liveSlot) && protectedLiveSlots.Contains(liveSlot))
                {
                    manifest.Entries.Remove(entry);
                    continue;
                }

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
                var sourceFileName = GetSourceVpkName(source.Source);
                var existingEntry = manifest.Entries.FirstOrDefault(entry =>
                    string.Equals(entry.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.RemoteId, source.Mod.RemoteId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.SourceFileName, sourceFileName, StringComparison.OrdinalIgnoreCase));
                if (existingEntry is not null)
                {
                    var existingLiveSlot = Path.GetFileName(existingEntry.LiveSlot);
                    if (!string.IsNullOrWhiteSpace(existingLiveSlot) && protectedLiveSlots.Contains(existingLiveSlot))
                        existingEntry = null;
                }

                var targetSlot = existingEntry?.LiveSlot;
                if (string.IsNullOrWhiteSpace(targetSlot))
                    targetSlot = ChooseLiveSlot(sourceFileName, usedLiveSlots);

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
                        SourceFileName = sourceFileName,
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

        public static StagingRepairResult RepairAppStagedVpks(string statePath, string gamePath, IReadOnlyCollection<DlmmMod> mods)
        {
            var result = new StagingRepairResult
            {
                ManifestPath = ManifestPath,
                RequiresDlmmApply = true,
                Preservation = RepairPreservationService.Preserve(statePath, gamePath)
            };

            var addonsPath = GetAddonsPath(gamePath);
            if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
                return result;

            VaultRandomizerSourceVpks(gamePath, addonsPath, mods);
            var protectedLiveSlots = BuildDlmmManagedRepairLiveSlots(gamePath, addonsPath, mods, result);
            ClearKnownRandomizerLiveSlots(addonsPath, mods.Where(IsRandomizerOwnedMod).ToList(), result, protectedLiveSlots);

            var manifest = LoadManifest();
            var entries = manifest.Entries
                .Where(entry => string.Equals(entry.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var entry in entries)
            {
                var liveSlot = Path.GetFileName(entry.LiveSlot);
                if (string.IsNullOrWhiteSpace(liveSlot))
                    continue;

                if (protectedLiveSlots.Contains(liveSlot))
                {
                    manifest.Entries.Remove(entry);
                    continue;
                }

                var livePath = Path.Combine(addonsPath, liveSlot);
                if (!File.Exists(livePath))
                {
                    result.MissingLiveVpkCount++;
                    result.RemovedManifestEntryCount++;
                    manifest.Entries.Remove(entry);
                    continue;
                }

                var currentHash = TryGetFileHash(livePath);
                if (!string.Equals(currentHash, entry.StagedHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedChangedLiveVpkCount++;
                    result.SkippedLiveVpks.Add(liveSlot);
                    continue;
                }

                File.Delete(livePath);
                result.RemovedLiveVpkCount++;
                result.RemovedManifestEntryCount++;
                result.RemovedLiveVpks.Add(liveSlot);
                manifest.Entries.Remove(entry);
            }

            RemoveHashMatchedRandomizerSkinVpks(gamePath, addonsPath, mods, result, protectedLiveSlots);
            RemoveUnexpectedLiveVpksAfterRepair(addonsPath, result, protectedLiveSlots);

            SaveManifest(manifest);
            return result;
        }

        private static HashSet<string> BuildDlmmManagedRepairLiveSlots(string gamePath, string addonsPath, IReadOnlyCollection<DlmmMod> mods, StagingRepairResult result)
        {
            var protectedMods = mods
                .Where(IsDlmmManagedRepairMod)
                .ToList();
            result.ExpectedDlmmManagedModCount = protectedMods.Count;
            var protectedLiveSlots = BuildDlmmManagedLiveSlotsForProtectedMods(gamePath, addonsPath, protectedMods);
            result.PreservedDlmmLiveVpkCount = protectedLiveSlots
                .Count(slot => File.Exists(Path.Combine(addonsPath, slot)));

            return protectedLiveSlots;
        }

        private static HashSet<string> BuildDlmmManagedLiveSlots(string gamePath, string addonsPath, IReadOnlyCollection<DlmmMod> mods)
        {
            return BuildDlmmManagedLiveSlotsForProtectedMods(
                gamePath,
                addonsPath,
                mods.Where(IsDlmmManagedRepairMod).ToList());
        }

        private static HashSet<string> BuildDlmmManagedLiveSlotsForProtectedMods(string gamePath, string addonsPath, IReadOnlyCollection<DlmmMod> protectedMods)
        {
            var protectedRemoteIds = protectedMods
                .Select(mod => mod.RemoteId)
                .Where(remoteId => !string.IsNullOrWhiteSpace(remoteId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var protectedLiveSlots = protectedMods
                .SelectMany(mod => mod.DlmmInstalledVpks.Concat(mod.InstalledVpks))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Where(IsLoadableLiveVpkName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var loggedSlot in GetProtectedLiveSlotsFromDlmmLog(gamePath, protectedRemoteIds))
            {
                protectedLiveSlots.Add(loggedSlot);
            }

            return protectedLiveSlots;
        }

        private static void RemoveUnexpectedLiveVpksAfterRepair(string addonsPath, StagingRepairResult result, HashSet<string> protectedLiveSlots)
        {
            foreach (var livePath in Directory.EnumerateFiles(addonsPath, "*.vpk", SearchOption.TopDirectoryOnly))
            {
                var liveSlot = Path.GetFileName(livePath);
                if (string.IsNullOrWhiteSpace(liveSlot) || IsRemotePrefixed(liveSlot))
                    continue;

                if (protectedLiveSlots.Contains(liveSlot))
                    continue;

                File.Delete(livePath);
                result.RemovedLiveVpkCount++;
                result.RemovedUnexpectedLiveVpkCount++;
                result.RemovedLiveVpks.Add(liveSlot);
                result.RemovedUnexpectedLiveVpks.Add(liveSlot);
            }
        }

        private static void RemoveHashMatchedRandomizerSkinVpks(string gamePath, string addonsPath, IReadOnlyCollection<DlmmMod> mods, StagingRepairResult result, HashSet<string> protectedLiveSlots)
        {
            var sourceHashes = mods
                .Where(IsRandomizerOwnedMod)
                .SelectMany(mod => BuildSourceSelection(gamePath, addonsPath, mod).Sources)
                .Select(file => TryGetFileHash(file.FullName))
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (sourceHashes.Count == 0)
                return;

            foreach (var livePath in Directory.EnumerateFiles(addonsPath, "*.vpk", SearchOption.TopDirectoryOnly))
            {
                var liveSlot = Path.GetFileName(livePath);
                if (string.IsNullOrWhiteSpace(liveSlot) || IsRemotePrefixed(liveSlot))
                    continue;

                if (protectedLiveSlots.Contains(liveSlot))
                    continue;

                var liveHash = TryGetFileHash(livePath);
                if (string.IsNullOrWhiteSpace(liveHash) || !sourceHashes.Contains(liveHash))
                    continue;

                File.Delete(livePath);
                result.RemovedLiveVpkCount++;
                result.RemovedMatchedSkinVpkCount++;
                result.RemovedLiveVpks.Add(liveSlot);
            }
        }

        private static void ClearKnownRandomizerLiveSlots(string addonsPath, IReadOnlyCollection<DlmmMod> mods, ApplyResult result)
        {
            ClearKnownRandomizerLiveSlots(addonsPath, mods, result, []);
        }

        private static void ClearKnownRandomizerLiveSlots(string addonsPath, IReadOnlyCollection<DlmmMod> mods, ApplyResult result, HashSet<string> protectedLiveSlots)
        {
            foreach (var liveSlot in GetKnownRandomizerLiveSlots(mods))
            {
                if (protectedLiveSlots.Contains(liveSlot))
                    continue;

                var livePath = Path.Combine(addonsPath, liveSlot);
                if (!File.Exists(livePath))
                    continue;

                File.Delete(livePath);
                result.StagedDisabledCount++;
            }
        }

        private static void ClearKnownRandomizerLiveSlots(string addonsPath, IReadOnlyCollection<DlmmMod> mods, StagingRepairResult result)
        {
            ClearKnownRandomizerLiveSlots(addonsPath, mods, result, []);
        }

        private static void ClearKnownRandomizerLiveSlots(string addonsPath, IReadOnlyCollection<DlmmMod> mods, StagingRepairResult result, HashSet<string> protectedLiveSlots)
        {
            foreach (var liveSlot in GetKnownRandomizerLiveSlots(mods))
            {
                if (protectedLiveSlots.Contains(liveSlot))
                    continue;

                var livePath = Path.Combine(addonsPath, liveSlot);
                if (!File.Exists(livePath))
                    continue;

                File.Delete(livePath);
                result.RemovedLiveVpkCount++;
                result.RemovedMatchedSkinVpkCount++;
                result.RemovedLiveVpks.Add(liveSlot);
            }
        }

        private static HashSet<string> GetKnownRandomizerLiveSlots(IReadOnlyCollection<DlmmMod> mods)
        {
            return mods
                .Where(IsRandomizerOwnedMod)
                .SelectMany(mod => mod.DlmmInstalledVpks.Concat(mod.InstalledVpks))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Where(name => IsLoadableLiveVpkName(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsRandomizerOwnedMod(DlmmMod mod)
        {
            return mod.IncludedInRandomizer
                && !string.IsNullOrWhiteSpace(mod.RemoteId)
                && string.IsNullOrWhiteSpace(mod.Folder)
                && !string.Equals(mod.Hero, "unknown", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVaultableSkinSourceMod(DlmmMod mod)
        {
            return IsRandomizerOwnedMod(mod)
                || (!mod.IsEnabledInDlmmProfile
                && !string.IsNullOrWhiteSpace(mod.RemoteId)
                && string.IsNullOrWhiteSpace(mod.Folder)
                && string.Equals(mod.Category, "Skins", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mod.Hero, "unknown", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDlmmManagedRepairMod(DlmmMod mod)
        {
            return mod.IsEnabledInDlmmProfile
                && !IsRandomizerOwnedMod(mod);
        }

        private static SourceSelection BuildSourceSelection(string gamePath, string addonsPath, DlmmMod mod)
        {
            var allRemoteSources = FindAllRemoteSourceVpks(gamePath, addonsPath, mod).ToList();
            if (allRemoteSources.Count == 0)
                return new SourceSelection(mod, [], 0);

            var dlmmSourceNames = BuildDlmmSourceNameSet(mod);
            var currentSources = dlmmSourceNames.Count == 0
                ? allRemoteSources
                : allRemoteSources
                    .Where(file => dlmmSourceNames.Contains(GetSourceVpkName(file)) || dlmmSourceNames.Contains(StripRemotePrefix(GetSourceVpkName(file))))
                    .ToList();

            var sourceVpks = currentSources.Count > 0
                ? currentSources
                : allRemoteSources;
            var staleSourceCount = currentSources.Count > 0
                ? allRemoteSources.Count - currentSources.Count
                : 0;
            var selectedSources = sourceVpks
                .GroupBy(file => StripRemotePrefix(GetSourceVpkName(file)), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ThenByDescending(file => file.Length)
                    .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .First())
                .OrderBy(file => GetSourceVpkName(file))
                .ToList();

            return new SourceSelection(mod, selectedSources, staleSourceCount + sourceVpks.Count - selectedSources.Count);
        }

        private static IEnumerable<FileInfo> FindAllRemoteSourceVpks(string gamePath, string addonsPath, DlmmMod mod)
        {
            if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
                return [];

            var loadableSources = Directory
                .EnumerateFiles(addonsPath, $"{mod.RemoteId}_*.vpk", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path));
            var quarantinedSources = Directory
                .EnumerateFiles(addonsPath, $"{mod.RemoteId}_*.vpk{QuarantinedSourceSuffix}", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path));
            var vaultDirectory = GetSourceVaultDirectory(gamePath);
            var vaultedSources = Directory.Exists(vaultDirectory)
                ? Directory
                    .EnumerateFiles(vaultDirectory, $"{mod.RemoteId}_*.vpk", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                : [];

            return loadableSources
                .Concat(quarantinedSources)
                .Concat(vaultedSources)
                .OrderBy(file => GetSourceVpkName(file))
                .ToList();
        }

        private static void VaultRandomizerSourceVpks(string gamePath, string addonsPath, IReadOnlyCollection<DlmmMod> mods)
        {
            var vaultDirectory = GetSourceVaultDirectory(gamePath);
            Directory.CreateDirectory(vaultDirectory);

            foreach (var mod in mods.Where(IsVaultableSkinSourceMod))
            {
                var sourcePaths = Directory
                    .EnumerateFiles(addonsPath, $"{mod.RemoteId}_*.vpk", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(addonsPath, $"{mod.RemoteId}_*.vpk{QuarantinedSourceSuffix}", SearchOption.TopDirectoryOnly))
                    .ToList();

                foreach (var source in sourcePaths)
                {
                    var sourceName = GetSourceVpkName(new FileInfo(source));
                    if (string.IsNullOrWhiteSpace(sourceName))
                        continue;

                    var vaultPath = Path.Combine(vaultDirectory, sourceName);
                    File.Copy(source, vaultPath, overwrite: true);
                    File.Delete(source);
                }
            }
        }

        private static string GetSourceVaultDirectory(string gamePath)
        {
            var gameKey = string.IsNullOrWhiteSpace(gamePath)
                ? "default"
                : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(gamePath.ToUpperInvariant())))[..16];
            return Path.Combine(SourceVaultRoot, gameKey);
        }

        private static string GetSourceVpkName(FileInfo file)
        {
            return file.Name.EndsWith(QuarantinedSourceSuffix, StringComparison.OrdinalIgnoreCase)
                ? file.Name[..^QuarantinedSourceSuffix.Length]
                : file.Name;
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

        private static IEnumerable<string> GetProtectedLiveSlotsFromDlmmLog(string gamePath, HashSet<string> protectedRemoteIds)
        {
            if (protectedRemoteIds.Count == 0)
                yield break;

            var addonsPath = GetAddonsPath(gamePath);
            var logPath = GetDlmmLogPath();
            if (string.IsNullOrWhiteSpace(addonsPath) || !File.Exists(logPath))
                yield break;

            foreach (var line in ReadLogLines(logPath).AsEnumerable().Reverse())
            {
                var enabledMatch = EnabledVpkLogRegex.Match(line);
                if (!enabledMatch.Success)
                    continue;

                var remoteId = enabledMatch.Groups["remoteId"].Value;
                if (!protectedRemoteIds.Contains(remoteId))
                    continue;

                var targetSlot = Path.GetFileName(enabledMatch.Groups["target"].Value.Trim());
                if (string.IsNullOrWhiteSpace(targetSlot) || IsRemotePrefixed(targetSlot))
                    continue;

                if (File.Exists(Path.Combine(addonsPath, targetSlot)))
                    yield return targetSlot;
            }
        }

        private static List<string> ReadLogLines(string logPath)
        {
            try
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                    lines.Add(line);

                return lines;
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
        }

        private static string GetDlmmLogPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dev.stormix.deadlock-mod-manager",
                "logs",
                "deadlock-mod-manager.log");
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

        private static readonly Regex EnabledVpkLogRegex =
            new(@"Enabled VPK for mod (?<remoteId>\d+):\s+.+?\s+->\s+(?<target>[^\\/:*?""<>|\s]+\.vpk)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
