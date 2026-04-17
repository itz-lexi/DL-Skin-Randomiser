using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class ModSelectionService
    {
        private static readonly Random Random = new();

        public static List<DlmmMod> RandomlySelectOnePerHero(IEnumerable<DlmmMod> mods)
        {
            var candidateMods = mods
                .Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId))
                .ToList();

            foreach (var mod in candidateMods)
            {
                mod.Hero = NormalizeHero(mod.Hero);
            }

            var selectedRemoteIds = candidateMods
                .Where(IsRandomizerCandidate)
                .GroupBy(mod => mod.Hero)
                .Select(group => group.ElementAt(Random.Next(group.Count())).RemoteId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ownedHeroKeys = candidateMods
                .Where(IsRandomizerOwnedMod)
                .Select(mod => mod.Hero)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in candidateMods.Where(mod => ownedHeroKeys.Contains(mod.Hero) && string.IsNullOrWhiteSpace(mod.Folder)))
            {
                mod.Enabled = IsRandomizerCandidate(mod) && selectedRemoteIds.Contains(mod.RemoteId);
            }

            return candidateMods;
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

        private static string NormalizeHero(string hero)
        {
            return string.IsNullOrWhiteSpace(hero)
                ? "unknown"
                : hero.Trim().ToLowerInvariant();
        }
    }
}
