using System.IO;
using System.Text.RegularExpressions;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static partial class AddonsInventoryService
    {
        public static AddonsReconciliationResult ApplyPhysicalState(string gamePath, IReadOnlyCollection<DlmmMod> mods)
        {
            var result = new AddonsReconciliationResult();
            var addonsPath = GetAddonsPath(gamePath);
            if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
                return result;

            var vpkFiles = Directory
                .EnumerateFiles(addonsPath, "*.vpk", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .ToList();

            var liveSlots = vpkFiles
                .Where(file => !RemotePrefixedVpkRegex().IsMatch(file.Name))
                .Select(file => file.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var prefixedSlotsByRemoteId = vpkFiles
                .Select(file => new
                {
                    file.Name,
                    Match = RemotePrefixedVpkRegex().Match(file.Name)
                })
                .Where(file => file.Match.Success)
                .GroupBy(file => file.Match.Groups["remoteId"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(file => file.Match.Groups["slot"].Value)
                        .Where(slot => !string.IsNullOrWhiteSpace(slot))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var prefixedFilesByRemoteId = vpkFiles
                .Select(file => new
                {
                    File = file,
                    Match = RemotePrefixedVpkRegex().Match(file.Name)
                })
                .Where(file => file.Match.Success)
                .GroupBy(file => file.Match.Groups["remoteId"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(file => file.File).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var knownSlotsByRemoteId = mods
                .Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId))
                .ToDictionary(
                    mod => mod.RemoteId,
                    mod => GetKnownSlots(mod, prefixedSlotsByRemoteId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            var logSlotOwners = GetLatestLogSlotOwners(gamePath, liveSlots);
            var appStagedLiveVpks = ManifestGameModStagingService.GetAppStagedLiveVpks(gamePath);

            var modsBySlot = knownSlotsByRemoteId
                .SelectMany(pair => pair.Value.Select(slot => new { RemoteId = pair.Key, Slot = slot }))
                .GroupBy(pair => pair.Slot, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(pair => pair.RemoteId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var mod in mods)
            {
                mod.ActiveVpkSlots = [];
                mod.IsAmbiguousInAddons = false;
                mod.Enabled = false;
                if (prefixedSlotsByRemoteId.TryGetValue(mod.RemoteId, out var prefixedSlots))
                {
                    mod.InstalledVpks = mod.InstalledVpks
                        .Concat(prefixedSlots)
                        .Where(slot => !string.IsNullOrWhiteSpace(slot))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }

            var unmatchedLiveSlots = new List<string>();
            foreach (var slot in liveSlots)
            {
                result.LiveSlotCount++;
                if (appStagedLiveVpks.TryGetValue(slot, out var appStagedVpk))
                {
                    var appStagedMod = mods.FirstOrDefault(candidate => string.Equals(candidate.RemoteId, appStagedVpk.RemoteId, StringComparison.OrdinalIgnoreCase));
                    if (appStagedMod is not null)
                    {
                        appStagedMod.Enabled = true;
                        appStagedMod.ActiveVpkSlots.Add(slot);
                        result.AppStagedModCount++;
                        result.Diagnostics.Add(BuildModDiagnostic(
                            appStagedMod,
                            "App staged",
                            slot,
                            "DL Skin Randomiser manifest",
                            $"The app staged this live VPK from {appStagedVpk.SourceFileName} and verified the file hash still matches its manifest.",
                            0,
                            "#B76DFF",
                            knownSlotsByRemoteId));
                        continue;
                    }
                }

                if (logSlotOwners.TryGetValue(slot, out var loggedRemoteId))
                {
                    var loggedMod = mods.FirstOrDefault(candidate => string.Equals(candidate.RemoteId, loggedRemoteId, StringComparison.OrdinalIgnoreCase));
                    if (loggedMod is not null)
                    {
                        loggedMod.Enabled = true;
                        loggedMod.ActiveVpkSlots.Add(slot);
                        result.LogMatchedModCount++;
                        result.Diagnostics.Add(BuildModDiagnostic(
                            loggedMod,
                            loggedMod.IsEnabledInDlmmProfile ? "Active" : "Live leftover",
                            slot,
                            "DLMM apply log",
                            loggedMod.IsEnabledInDlmmProfile
                                ? $"DLMM last recorded this live slot as owned by remoteId {loggedRemoteId}."
                                : $"This live file still exists, but DLMM state does not currently mark remoteId {loggedRemoteId} enabled.",
                            0,
                            loggedMod.IsEnabledInDlmmProfile ? "#91D18B" : "#F0B86E",
                            knownSlotsByRemoteId));
                        continue;
                    }
                }

                var hashMatchedMod = FindExactHashMatch(addonsPath, slot, mods, prefixedFilesByRemoteId);
                if (hashMatchedMod is not null)
                {
                    hashMatchedMod.Enabled = true;
                    hashMatchedMod.ActiveVpkSlots.Add(slot);
                    result.HashMatchedModCount++;
                    result.Diagnostics.Add(BuildModDiagnostic(
                        hashMatchedMod,
                        hashMatchedMod.IsEnabledInDlmmProfile ? "Active" : "Live leftover",
                        slot,
                        "Exact VPK file match",
                        hashMatchedMod.IsEnabledInDlmmProfile
                            ? "The live VPK has the same size and hash as this mod's stored VPK file."
                            : "The live VPK has the same size and hash as this mod's stored VPK file, but DLMM state does not mark it enabled.",
                        1,
                        hashMatchedMod.IsEnabledInDlmmProfile ? "#8FB9F2" : "#F0B86E",
                        knownSlotsByRemoteId));
                    continue;
                }

                if (!modsBySlot.TryGetValue(slot, out var candidateRemoteIds) || candidateRemoteIds.Count == 0)
                {
                    result.UnmatchedLiveSlotCount++;
                    unmatchedLiveSlots.Add(slot);
                    continue;
                }

                if (candidateRemoteIds.Count == 1)
                {
                    var mod = mods.FirstOrDefault(candidate => string.Equals(candidate.RemoteId, candidateRemoteIds[0], StringComparison.OrdinalIgnoreCase));
                    if (mod is null)
                    {
                        result.UnmatchedLiveSlotCount++;
                        continue;
                    }

                    mod.ActiveVpkSlots.Add(slot);
                    if (mod.IsEnabledInDlmmProfile)
                    {
                        mod.Enabled = true;
                        result.ConfirmedModCount++;
                        result.Diagnostics.Add(BuildModDiagnostic(
                            mod,
                            "Likely active",
                            slot,
                            "Unique slot plus DLMM state",
                            "Only one loaded mod knows this live VPK slot, and DLMM state also marks it enabled.",
                            1,
                            "#8FB9F2",
                            knownSlotsByRemoteId));
                    }
                    else
                    {
                        result.SlotOnlyGuessCount++;
                        result.Diagnostics.Add(BuildModDiagnostic(
                            mod,
                            "Slot-only guess",
                            slot,
                            "Unique slot name only",
                            "This live VPK matches one mod's known slot, but DLMM state does not mark that mod enabled. Treat this as a weak clue, not an active mod.",
                            4,
                            "#F0B86E",
                            knownSlotsByRemoteId));
                    }

                    continue;
                }

                var enabledProfileCandidates = mods
                    .Where(mod => mod.IsEnabledInDlmmProfile && candidateRemoteIds.Contains(mod.RemoteId, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (enabledProfileCandidates.Count == 1)
                {
                    enabledProfileCandidates[0].Enabled = true;
                    enabledProfileCandidates[0].ActiveVpkSlots.Add(slot);
                    result.ProfileDisambiguatedModCount++;
                    result.Diagnostics.Add(BuildModDiagnostic(
                        enabledProfileCandidates[0],
                        "Likely active",
                        slot,
                        "Shared slot plus DLMM profile",
                        "Multiple mods know this slot, but only one candidate is enabled in DLMM's profile state.",
                        2,
                        "#D5B56E",
                        knownSlotsByRemoteId));
                    continue;
                }

                result.AmbiguousLiveSlotCount++;
                foreach (var mod in mods.Where(mod => candidateRemoteIds.Contains(mod.RemoteId, StringComparer.OrdinalIgnoreCase)))
                {
                    mod.IsAmbiguousInAddons = true;
                    result.Diagnostics.Add(BuildModDiagnostic(
                        mod,
                        "Ambiguous",
                        slot,
                        "Shared live slot",
                        "Multiple loaded mods could own this live slot, so the app will not guess.",
                        3,
                        "#F0B86E",
                        knownSlotsByRemoteId));
                }
            }

            foreach (var slot in unmatchedLiveSlots)
            {
                result.Diagnostics.Add(new AddonsDiagnosticItem
                {
                    Status = "Unmatched live VPK",
                    LiveSlot = slot,
                    Evidence = "Game file exists",
                    Detail = "This unprefixed VPK is loaded by the game, but it did not match DLMM's recent apply log or loaded mod slot metadata.",
                    SortRank = 4,
                    StatusBrush = "#F07C7C"
                });
            }

            var stateOnlyMods = mods
                .Where(mod => mod.IsEnabledInDlmmProfile && !mod.Enabled)
                .OrderBy(mod => mod.Name)
                .ToList();
            result.StateOnlyModCount = stateOnlyMods.Count;
            if (stateOnlyMods.Count > 0)
            {
                result.Diagnostics.Add(new AddonsDiagnosticItem
                {
                    Status = "Stale DLMM state",
                    ModName = $"{stateOnlyMods.Count} mods marked enabled only in state.json",
                    LiveSlot = "",
                    Evidence = "enabledMods says true",
                    Detail = "These mods are still marked enabled in DLMM state, but no live addon VPK evidence matched them. This is usually stale DLMM state, so diagnostics no longer lists each one as active.",
                    RemoteId = string.Join(", ", stateOnlyMods.Take(8).Select(mod => mod.RemoteId)),
                    SortRank = 5,
                    StatusBrush = "#AEB7BA"
                });
            }

            result.Diagnostics = result.Diagnostics
                .OrderBy(item => item.SortRank)
                .ThenBy(item => item.ModName)
                .ThenBy(item => item.LiveSlot)
                .ToList();

            return result;
        }

        private static AddonsDiagnosticItem BuildModDiagnostic(
            DlmmMod mod,
            string status,
            string liveSlot,
            string evidence,
            string detail,
            int sortRank,
            string statusBrush,
            IReadOnlyDictionary<string, HashSet<string>> knownSlotsByRemoteId)
        {
            var storedSlots = knownSlotsByRemoteId.TryGetValue(mod.RemoteId, out var slots)
                ? string.Join(", ", slots.OrderBy(slot => slot, StringComparer.OrdinalIgnoreCase))
                : "";

            return new AddonsDiagnosticItem
            {
                Status = status,
                ModName = mod.Name,
                RemoteId = mod.RemoteId,
                LiveSlot = liveSlot,
                StoredSlots = storedSlots,
                Evidence = evidence,
                Detail = detail,
                SortRank = sortRank,
                StatusBrush = statusBrush
            };
        }

        private static Dictionary<string, string> GetLatestLogSlotOwners(string gamePath, HashSet<string> liveSlots)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var logPath = GetDlmmLogPath();
            if (!File.Exists(logPath))
                return result;

            var lines = ReadLogLines(logPath);
            var liveSlotSet = liveSlots.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.AsEnumerable().Reverse())
            {
                if (result.Count >= liveSlotSet.Count)
                    break;

                var enabledMatch = EnabledVpkLogRegex().Match(line);
                if (enabledMatch.Success)
                {
                    var remoteId = enabledMatch.Groups["remoteId"].Value;
                    var targetSlot = Path.GetFileName(enabledMatch.Groups["target"].Value.Trim());
                    if (liveSlotSet.Contains(targetSlot) && !result.ContainsKey(targetSlot) && LiveSlotExists(gamePath, targetSlot))
                        result[targetSlot] = remoteId;

                    continue;
                }

                var removedMatch = RemovedLiveVpkLogRegex().Match(line);
                if (removedMatch.Success)
                {
                    var targetSlot = Path.GetFileName(removedMatch.Groups["target"].Value.Trim());
                    if (liveSlotSet.Contains(targetSlot) && !result.ContainsKey(targetSlot) && !LiveSlotExists(gamePath, targetSlot))
                        result[targetSlot] = "";
                }
            }

            return result
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> ReadLogLines(string logPath)
        {
            try
            {
                using var stream = new FileStream(
                    logPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                {
                    lines.Add(line);
                }

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

        private static DlmmMod? FindExactHashMatch(
            string addonsPath,
            string liveSlot,
            IReadOnlyCollection<DlmmMod> mods,
            IReadOnlyDictionary<string, List<FileInfo>> prefixedFilesByRemoteId)
        {
            var livePath = Path.Combine(addonsPath, liveSlot);
            if (!File.Exists(livePath))
                return null;

            FileInfo liveFile;
            try
            {
                liveFile = new FileInfo(livePath);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            var candidates = mods
                .Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId)
                    && prefixedFilesByRemoteId.TryGetValue(mod.RemoteId, out var files)
                    && files.Any(file => file.Length == liveFile.Length))
                .ToList();

            if (candidates.Count == 0)
                return null;

            var liveHash = TryGetFileHash(livePath);
            if (string.IsNullOrWhiteSpace(liveHash))
                return null;

            foreach (var mod in candidates)
            {
                if (!prefixedFilesByRemoteId.TryGetValue(mod.RemoteId, out var files))
                    continue;

                foreach (var file in files.Where(file => file.Length == liveFile.Length))
                {
                    var storedHash = TryGetFileHash(file.FullName);
                    if (string.Equals(storedHash, liveHash, StringComparison.OrdinalIgnoreCase))
                        return mod;
                }
            }

            return null;
        }

        private static string TryGetFileHash(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var hash = System.Security.Cryptography.SHA256.HashData(stream);
                return Convert.ToHexString(hash);
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

        private static bool LiveSlotExists(string gamePath, string slotName)
        {
            var addonsPath = GetAddonsPath(gamePath);
            return !string.IsNullOrWhiteSpace(addonsPath)
                && File.Exists(Path.Combine(addonsPath, Path.GetFileName(slotName)));
        }

        private static IEnumerable<string> GetKnownSlots(DlmmMod mod, IReadOnlyDictionary<string, List<string>> prefixedSlotsByRemoteId)
        {
            foreach (var slot in mod.InstalledVpks.Select(Path.GetFileName))
            {
                if (!string.IsNullOrWhiteSpace(slot))
                    yield return slot;
            }

            if (!prefixedSlotsByRemoteId.TryGetValue(mod.RemoteId, out var prefixedSlots))
                yield break;

            foreach (var slot in prefixedSlots)
            {
                if (!string.IsNullOrWhiteSpace(slot))
                    yield return slot;
            }
        }

        private static string GetAddonsPath(string gamePath)
        {
            return string.IsNullOrWhiteSpace(gamePath)
                ? ""
                : Path.Combine(gamePath, "game", "citadel", "addons");
        }

        private static string GetDlmmLogPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dev.stormix.deadlock-mod-manager",
                "logs",
                "deadlock-mod-manager.log");
        }

        [GeneratedRegex(@"^(?<remoteId>\d+)_(?<slot>.+\.vpk)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex RemotePrefixedVpkRegex();

        [GeneratedRegex(@"Enabled VPK for mod (?<remoteId>\d+):\s+.+?\s+->\s+(?<target>[^\\/:*?""<>|\s]+\.vpk)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex EnabledVpkLogRegex();

        [GeneratedRegex(@"Removing file:\s+""(?<target>[^""]+\.vpk)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex RemovedLiveVpkLogRegex();
    }

    public class AddonsReconciliationResult
    {
        public List<AddonsDiagnosticItem> Diagnostics { get; set; } = [];
        public int LiveSlotCount { get; set; }
        public int AppStagedModCount { get; set; }
        public int LogMatchedModCount { get; set; }
        public int HashMatchedModCount { get; set; }
        public int ConfirmedModCount { get; set; }
        public int ProfileDisambiguatedModCount { get; set; }
        public int SlotOnlyGuessCount { get; set; }
        public int StateOnlyModCount { get; set; }
        public int AmbiguousLiveSlotCount { get; set; }
        public int UnmatchedLiveSlotCount { get; set; }
    }
}
