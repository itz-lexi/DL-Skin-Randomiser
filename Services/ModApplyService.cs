using DL_Skin_Randomiser.Models;
using System.IO;

namespace DL_Skin_Randomiser.Services
{
    public static class ModApplyService
    {
        public static ApplyResult Apply(
            string statePath,
            string gamePath,
            IReadOnlyCollection<DlmmMod> mods,
            string selectedProfileId,
            IReadOnlyCollection<DlmmMod>? sourceVaultMods = null)
        {
            var profileMods = mods
                .Where(mod => mod.IsInSelectedProfile)
                .Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId))
                .ToList();

            foreach (var mod in profileMods)
            {
                mod.Hero = NormalizeHero(mod.Hero);
            }

            var ownedHeroGroups = profileMods
                .Where(IsRandomizerOwnedMod)
                .GroupBy(mod => mod.Hero)
                .ToList();

            var forcedDisabledCount = 0;
            foreach (var heroGroup in ownedHeroGroups)
            {
                var enabledMods = heroGroup
                    .Where(mod => mod.IncludedInRandomizer)
                    .Where(mod => mod.Enabled)
                    .ToList();
                if (enabledMods.Count <= 1)
                    continue;

                var keptMod = enabledMods[Random.Shared.Next(enabledMods.Count)];
                foreach (var extraMod in enabledMods.Where(mod => !ReferenceEquals(mod, keptMod)))
                {
                    extraMod.Enabled = false;
                    forcedDisabledCount++;
                }
            }

            var ownedHeroKeys = ownedHeroGroups
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Apply only owns normal hero groups with at least one randomiser-included mod.
            // Everything else keeps DLMM's existing enabled state.
            foreach (var mod in profileMods)
            {
                if (ShouldPreserveDlmmEnabledState(mod, ownedHeroKeys))
                {
                    mod.Enabled = mod.IsEnabledInDlmmProfile;
                    continue;
                }

                if (!mod.IncludedInRandomizer)
                    mod.Enabled = false;
            }

            var backupPath = DlmmStateService.SaveEnabledMods(statePath, profileMods, selectedProfileId);
            var stagingResult = ManifestGameModStagingService.Stage(gamePath, profileMods, sourceVaultMods ?? profileMods);

            return new ApplyResult
            {
                WrittenCount = profileMods.Count,
                EnabledCount = profileMods.Count(mod => mod.Enabled),
                ForcedDisabledCount = forcedDisabledCount,
                StagedEnabledCount = stagingResult.StagedEnabledCount,
                StagedDisabledCount = stagingResult.StagedDisabledCount,
                StagingSkippedCount = stagingResult.StagingSkippedCount,
                EnabledModsWithoutStagedFilesCount = stagingResult.EnabledModsWithoutStagedFilesCount,
                EnabledModsWithoutStagedFiles = stagingResult.EnabledModsWithoutStagedFiles,
                StaleSourceVpkSkippedCount = stagingResult.StaleSourceVpkSkippedCount,
                GameFilesStaged = stagingResult.GameFilesStaged,
                RequiresDlmmApply = stagingResult.RequiresDlmmApply,
                BackupPath = backupPath,
                AddonsBackupPath = stagingResult.AddonsBackupPath
            };
        }

        private static string NormalizeHero(string hero)
        {
            return string.IsNullOrWhiteSpace(hero)
                ? "unknown"
                : hero.Trim().ToLowerInvariant();
        }

        private static bool IsRandomizerCandidate(DlmmMod mod)
        {
            return IsRandomizerOwnedMod(mod)
                && mod.InstalledVpks.Count > 0;
        }

        private static bool IsRandomizerOwnedMod(DlmmMod mod)
        {
            return mod.IncludedInRandomizer
                && mod.Hero != "unknown"
                && string.IsNullOrWhiteSpace(mod.Folder);
        }

        private static bool ShouldPreserveDlmmEnabledState(DlmmMod mod, HashSet<string> ownedHeroKeys)
        {
            if (!string.IsNullOrWhiteSpace(mod.Folder))
                return true;

            if (mod.Hero == "unknown")
                return true;

            if (mod.IncludedInRandomizer)
                return false;

            return !ownedHeroKeys.Contains(mod.Hero);
        }
    }
}
