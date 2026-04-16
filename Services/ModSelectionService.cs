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
                .Where(mod => mod.IncludedInRandomizer && mod.Hero != "unknown")
                .GroupBy(mod => mod.Hero)
                .Select(group => group.ElementAt(Random.Next(group.Count())).RemoteId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in candidateMods.Where(mod => mod.IncludedInRandomizer && mod.Hero != "unknown"))
            {
                mod.Enabled = selectedRemoteIds.Contains(mod.RemoteId);
            }

            return candidateMods;
        }

        private static string NormalizeHero(string hero)
        {
            return string.IsNullOrWhiteSpace(hero)
                ? "unknown"
                : hero.Trim().ToLowerInvariant();
        }
    }
}
