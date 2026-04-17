using DL_Skin_Randomiser.Models;
using System.IO;

namespace DL_Skin_Randomiser.Services
{
    public static class ModApplyService
    {
        public static ApplyResult Apply(string statePath, string gamePath, IReadOnlyCollection<DlmmMod> mods, string selectedProfileId)
        {
            foreach (var mod in mods)
            {
                mod.Hero = NormalizeHero(mod.Hero);
            }

            var heroMods = mods
                .Where(mod => mod.IncludedInRandomizer && mod.Hero != "unknown")
                .ToList();

            var forcedDisabledCount = 0;
            foreach (var heroGroup in heroMods.GroupBy(mod => mod.Hero))
            {
                var enabledMods = heroGroup.Where(mod => mod.Enabled).ToList();
                if (enabledMods.Count <= 1)
                    continue;

                foreach (var extraMod in enabledMods.Skip(1))
                {
                    extraMod.Enabled = false;
                    forcedDisabledCount++;
                }
            }

            var profileMods = mods
                .Where(mod => mod.IsInSelectedProfile)
                .Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId))
                .ToList();
            var backupPath = DlmmStateService.SaveEnabledMods(statePath, profileMods, selectedProfileId);
            var stagingResult = GameModStagingService.Stage(gamePath, profileMods);

            return new ApplyResult
            {
                WrittenCount = profileMods.Count,
                EnabledCount = profileMods.Count(mod => mod.Enabled),
                ForcedDisabledCount = forcedDisabledCount,
                StagedEnabledCount = stagingResult.StagedEnabledCount,
                StagedDisabledCount = stagingResult.StagedDisabledCount,
                StagingSkippedCount = stagingResult.StagingSkippedCount,
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
    }
}
