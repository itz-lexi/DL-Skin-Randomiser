using System.IO;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class GameModStagingService
    {
        public static ApplyResult Stage(string gamePath, IReadOnlyCollection<DlmmMod> mods)
        {
            var result = new ApplyResult();
            var addonsPath = GetAddonsPath(gamePath);
            if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
            {
                result.StagingSkippedCount = mods.Count;
                return result;
            }

            result.AddonsBackupPath = BackupAddons(addonsPath);

            var profileMods = mods
                .Where(mod => mod.IsInSelectedProfile && !string.IsNullOrWhiteSpace(mod.RemoteId))
                .ToList();

            foreach (var mod in profileMods.Where(mod => !mod.Enabled))
            {
                var knownSlots = GetKnownSlotNames(addonsPath, mod).ToList();
                if (knownSlots.Count == 0)
                {
                    result.StagingSkippedCount++;
                    continue;
                }

                foreach (var slotName in knownSlots)
                {
                    if (DisableVpk(addonsPath, mod.RemoteId, slotName))
                        result.StagedDisabledCount++;
                }
            }

            foreach (var mod in profileMods.Where(mod => mod.Enabled))
            {
                var disabledVpks = FindDisabledVpks(addonsPath, mod.RemoteId).ToList();
                var knownSlots = GetKnownSlotNames(addonsPath, mod).ToList();
                if (knownSlots.Count == 0 && disabledVpks.Count == 0)
                {
                    result.StagingSkippedCount++;
                    continue;
                }

                if (mod.Enabled)
                {
                    var enabledAny = false;
                    foreach (var disabledVpk in disabledVpks)
                    {
                        if (EnableVpk(disabledVpk, mod.RemoteId))
                        {
                            result.StagedEnabledCount++;
                            enabledAny = true;
                        }
                    }

                    if (!enabledAny)
                    {
                        var restoredVpks = RestoreDisabledVpksFromBackup(addonsPath, mod.RemoteId).ToList();
                        foreach (var restoredVpk in restoredVpks)
                        {
                            result.StagedEnabledCount++;
                            enabledAny = true;
                        }

                        foreach (var slotName in knownSlots.Where(slotName => !restoredVpks.Contains(slotName, StringComparer.OrdinalIgnoreCase)))
                        {
                            if (DisableVpk(addonsPath, mod.RemoteId, slotName))
                                result.StagedDisabledCount++;
                        }
                    }

                    if (!enabledAny && knownSlots.Any(slotName => File.Exists(Path.Combine(addonsPath, slotName))))
                        enabledAny = true;

                    if (!enabledAny)
                        result.StagingSkippedCount++;

                    continue;
                }
            }

            return result;
        }

        private static string GetAddonsPath(string gamePath)
        {
            return string.IsNullOrWhiteSpace(gamePath)
                ? ""
                : Path.Combine(gamePath, "game", "citadel", "addons");
        }

        private static string BackupAddons(string addonsPath)
        {
            var backupRoot = Path.Combine(Path.GetDirectoryName(addonsPath) ?? addonsPath, "addons-backups");
            var backupPath = Path.Combine(backupRoot, $"addons-backup-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
            Directory.CreateDirectory(backupPath);

            foreach (var vpkPath in Directory.EnumerateFiles(addonsPath, "*.vpk", SearchOption.TopDirectoryOnly))
            {
                File.Copy(vpkPath, Path.Combine(backupPath, Path.GetFileName(vpkPath)), overwrite: false);
            }

            return backupPath;
        }

        private static bool EnableVpk(string disabledVpkPath, string remoteId)
        {
            var disabledFileName = Path.GetFileName(disabledVpkPath);
            var prefix = $"{remoteId}_";
            if (!disabledFileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var targetFileName = Path.GetFileName(disabledFileName[prefix.Length..]);
            if (string.IsNullOrWhiteSpace(targetFileName))
                return false;

            var targetPath = Path.Combine(Path.GetDirectoryName(disabledVpkPath) ?? "", targetFileName);
            if (File.Exists(targetPath))
                return false;

            File.Move(disabledVpkPath, targetPath);
            return true;
        }

        private static bool DisableVpk(string addonsPath, string remoteId, string targetVpkName)
        {
            var safeTargetVpkName = Path.GetFileName(targetVpkName);

            if (string.IsNullOrWhiteSpace(safeTargetVpkName))
                return false;

            var targetPath = Path.Combine(addonsPath, safeTargetVpkName);
            if (!File.Exists(targetPath))
                return false;

            var disabledPath = Path.Combine(addonsPath, $"{remoteId}_{safeTargetVpkName}");
            if (File.Exists(disabledPath))
                return false;

            File.Move(targetPath, disabledPath);
            return true;
        }

        private static IEnumerable<string> FindDisabledVpks(string addonsPath, string remoteId)
        {
            return Directory
                .EnumerateFiles(addonsPath, $"{remoteId}_*.vpk", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path));
        }

        private static IEnumerable<string> GetKnownSlotNames(string addonsPath, DlmmMod mod)
        {
            var slotNames = new HashSet<string>(
                mod.InstalledVpks
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!),
                StringComparer.OrdinalIgnoreCase);

            foreach (var disabledVpk in FindDisabledVpks(addonsPath, mod.RemoteId))
            {
                var slotName = StripRemotePrefix(Path.GetFileName(disabledVpk), mod.RemoteId);
                if (!string.IsNullOrWhiteSpace(slotName))
                    slotNames.Add(slotName);
            }

            foreach (var backupSlotName in FindBackupSlotNames(addonsPath, mod.RemoteId))
            {
                slotNames.Add(backupSlotName);
            }

            return slotNames;
        }

        private static IEnumerable<string> FindBackupSlotNames(string addonsPath, string remoteId)
        {
            var backupRoot = Path.Combine(Path.GetDirectoryName(addonsPath) ?? addonsPath, "addons-backups");
            if (!Directory.Exists(backupRoot))
                yield break;

            foreach (var backupFile in Directory.EnumerateFiles(backupRoot, $"{remoteId}_*.vpk", SearchOption.AllDirectories))
            {
                var slotName = StripRemotePrefix(Path.GetFileName(backupFile), remoteId);
                if (!string.IsNullOrWhiteSpace(slotName))
                    yield return slotName;
            }
        }

        private static string StripRemotePrefix(string fileName, string remoteId)
        {
            var prefix = $"{remoteId}_";
            return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? fileName[prefix.Length..]
                : "";
        }

        private static IEnumerable<string> RestoreDisabledVpksFromBackup(string addonsPath, string remoteId)
        {
            var backupRoot = Path.Combine(Path.GetDirectoryName(addonsPath) ?? addonsPath, "addons-backups");
            if (!Directory.Exists(backupRoot))
                yield break;

            var restoredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var backupFiles = Directory
                .EnumerateFiles(backupRoot, $"{remoteId}_*.vpk", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            foreach (var backupFile in backupFiles)
            {
                var targetFileName = StripRemotePrefix(backupFile.Name, remoteId);
                if (string.IsNullOrWhiteSpace(targetFileName))
                    continue;
                if (!restoredTargets.Add(targetFileName))
                    continue;

                var targetPath = Path.Combine(addonsPath, targetFileName);
                if (File.Exists(targetPath))
                    continue;

                File.Copy(backupFile.FullName, targetPath);
                yield return targetFileName;
            }
        }

    }
}
