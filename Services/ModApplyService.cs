using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class ModApplyService
    {
        public static void Apply(string statePath, IReadOnlyCollection<DlmmMod> mods, string selectedProfileId)
        {
            foreach (var mod in mods)
            {
                mod.Hero = NormalizeHero(mod.Hero);
            }

            var heroMods = mods
                .Where(mod => mod.IncludedInRandomizer && mod.Hero != "unknown")
                .ToList();

            foreach (var heroGroup in heroMods.GroupBy(mod => mod.Hero))
            {
                var enabledMods = heroGroup.Where(mod => mod.Enabled).ToList();
                if (enabledMods.Count <= 1)
                    continue;

                foreach (var extraMod in enabledMods.Skip(1))
                    extraMod.Enabled = false;
            }

            DlmmStateService.SaveEnabledMods(statePath, heroMods, selectedProfileId);
        }

        private static string NormalizeHero(string hero)
        {
            return string.IsNullOrWhiteSpace(hero)
                ? "unknown"
                : hero.Trim().ToLowerInvariant();
        }
    }
}
