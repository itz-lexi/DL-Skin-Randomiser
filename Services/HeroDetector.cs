using System.Text.RegularExpressions;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class HeroDetector
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["abrams"] = "abrams",
            ["apollo"] = "apollo",
            ["bebop"] = "bebop",
            ["billy"] = "billy",
            ["calico"] = "calico",
            ["celeste"] = "celeste",
            ["the doorman"] = "doorman",
            ["doorman"] = "doorman",
            ["drifter"] = "drifter",
            ["dynamo"] = "dynamo",
            ["graves"] = "graves",
            ["grey talon"] = "grey talon",
            ["gray talon"] = "grey talon",
            ["haze"] = "haze",
            ["holliday"] = "holliday",
            ["holiday"] = "holliday",
            ["infernus"] = "infernus",
            ["ivy"] = "ivy",
            ["kelvin"] = "kelvin",
            ["lady geist"] = "lady geist",
            ["lash"] = "lash",
            ["mcginnis"] = "mcginnis",
            ["mina"] = "mina",
            ["mirage"] = "mirage",
            ["mo and krill"] = "mo & krill",
            ["mo & krill"] = "mo & krill",
            ["paige"] = "paige",
            ["paradox"] = "paradox",
            ["pocket"] = "pocket",
            ["rem"] = "rem",
            ["seven"] = "seven",
            ["shiv"] = "shiv",
            ["silver"] = "silver",
            ["sinclair"] = "sinclair",
            ["venator"] = "venator",
            ["victor"] = "victor",
            ["vindicta"] = "vindicta",
            ["viscous"] = "viscous",
            ["viper"] = "vyper",
            ["vyper"] = "vyper",
            ["warden"] = "warden",
            ["wraith"] = "wraith",
            ["yamato"] = "yamato"
        };

        public static List<string> KnownHeroes => Aliases.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(hero => hero)
            .ToList();

        public static List<CharacterOption> KnownCharacterOptions => KnownHeroes
            .Select(hero => new CharacterOption
            {
                Key = hero,
                Name = HeroDisplayService.ToDisplayName(hero)
            })
            .ToList();

        public static string Detect(string modName)
        {
            if (string.IsNullOrWhiteSpace(modName))
                return "unknown";

            var hero = Aliases
                .OrderByDescending(alias => alias.Key.Length)
                .FirstOrDefault(alias => ContainsPhrase(modName, alias.Key));

            return string.IsNullOrWhiteSpace(hero.Value)
                ? "unknown"
                : hero.Value;
        }

        private static bool ContainsPhrase(string value, string phrase)
        {
            var pattern = $@"(?<![a-z0-9]){Regex.Escape(phrase)}(?![a-z0-9])";
            return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
