using System.Globalization;

namespace DL_Skin_Randomiser.Services
{
    public static class HeroDisplayService
    {
        private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mo & krill"] = "Mo & Krill",
            ["unknown"] = "Unsorted"
        };

        public static string ToKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";

            return value.Trim()
                .ToLowerInvariant()
                .Replace(" and ", " & ");
        }

        public static string ToDisplayName(string key)
        {
            var normalized = ToKey(key);

            if (DisplayNames.TryGetValue(normalized, out var displayName))
                return displayName;

            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized);
        }

        public static string ToFolderDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var trimmed = value.Trim();
            if (trimmed.Any(char.IsUpper))
                return trimmed;

            var normalized = ToKey(trimmed);
            if (!normalized.Contains(' ') && normalized.Length <= 4)
                return normalized.ToUpperInvariant();

            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized);
        }
    }
}
