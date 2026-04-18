using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class ModSelectionService
    {
        private static readonly Random Random = new();

        public static List<DlmmMod> RandomlySelectOnePerHero(
            IEnumerable<DlmmMod> mods,
            IReadOnlySet<string>? stageableRemoteIds = null,
            IReadOnlyCollection<LoadoutPick>? previousLoadout = null)
        {
            var candidateMods = mods
                .Where(mod => !string.IsNullOrWhiteSpace(mod.RemoteId))
                .ToList();

            foreach (var mod in candidateMods)
            {
                mod.Hero = NormalizeHero(mod.Hero);
            }

            var previousRemoteIdsByHero = (previousLoadout ?? [])
                .Where(pick => !string.IsNullOrWhiteSpace(pick.RemoteId))
                .GroupBy(pick => NormalizeHero(pick.Hero))
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(pick => pick.RemoteId).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            var selectedRemoteIds = candidateMods
                .Where(IsRandomizerCandidate)
                .Where(mod => stageableRemoteIds is null || stageableRemoteIds.Contains(mod.RemoteId))
                .GroupBy(mod => mod.Hero)
                .Select(group =>
                {
                    var candidates = group.ToList();
                    if (previousRemoteIdsByHero.TryGetValue(group.Key, out var previousRemoteIds) && candidates.Count > 1)
                    {
                        var freshCandidates = candidates
                            .Where(mod => !previousRemoteIds.Contains(mod.RemoteId))
                            .ToList();
                        if (freshCandidates.Count > 0)
                            candidates = freshCandidates;
                    }

                    return candidates[Random.Next(candidates.Count)].RemoteId;
                })
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
            return IsRandomizerOwnedMod(mod);
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
