using System.IO;
using System.Text;
using System.Diagnostics;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class AppDiagnosticLogService
    {
        public static string LogDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DL-Skin-Randomiser",
                "Logs");

        public static string CurrentLogPath =>
            Path.Combine(LogDirectory, $"diagnostics-{DateTime.Now:yyyyMMdd}.log");

        [Conditional("DEBUG")]
        public static void WriteSnapshot(
            string action,
            string statePath,
            string gamePath,
            string profileId,
            string profileName,
            IReadOnlyCollection<DlmmMod> mods,
            IReadOnlyCollection<LoadoutPick> loadout,
            AddonsReconciliationResult addonsState,
            string message = "",
            Exception? exception = null)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    CurrentLogPath,
                    BuildSnapshot(action, statePath, gamePath, profileId, profileName, mods, loadout, addonsState, message, exception),
                    Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never block the app's main workflow.
            }
        }

        private static string BuildSnapshot(
            string action,
            string statePath,
            string gamePath,
            string profileId,
            string profileName,
            IReadOnlyCollection<DlmmMod> mods,
            IReadOnlyCollection<LoadoutPick> loadout,
            AddonsReconciliationResult addonsState,
            string message,
            Exception? exception)
        {
            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine("============================================================");
            builder.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {action}");
            builder.AppendLine("============================================================");

            if (!string.IsNullOrWhiteSpace(message))
                builder.AppendLine($"Message: {message}");

            builder.AppendLine($"Profile: {profileName} ({profileId})");
            builder.AppendLine($"State path: {statePath}");
            builder.AppendLine($"Game path: {gamePath}");
            builder.AppendLine($"Loaded mods: {mods.Count}");
            builder.AppendLine($"Selected in app: {mods.Count(mod => mod.Enabled)}");
            builder.AppendLine($"Included in randomiser: {mods.Count(mod => mod.IncludedInRandomizer)}");
            builder.AppendLine($"Protected/custom folder mods: {mods.Count(mod => !string.IsNullOrWhiteSpace(mod.Folder))}");
            builder.AppendLine($"Unknown/unsorted mods: {mods.Count(mod => string.Equals(mod.Hero, "unknown", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(mod.Folder))}");

            builder.AppendLine();
            builder.AppendLine("Addons diagnostics:");
            builder.AppendLine($"  Live VPKs: {addonsState.LiveSlotCount}");
            builder.AppendLine($"  App staged: {addonsState.AppStagedModCount}");
            builder.AppendLine($"  Log matched: {addonsState.LogMatchedModCount}");
            builder.AppendLine($"  Hash matched: {addonsState.HashMatchedModCount}");
            builder.AppendLine($"  Likely live: {addonsState.ConfirmedModCount + addonsState.ProfileDisambiguatedModCount}");
            builder.AppendLine($"  Weak guesses: {addonsState.SlotOnlyGuessCount}");
            builder.AppendLine($"  Ambiguous: {addonsState.AmbiguousLiveSlotCount}");
            builder.AppendLine($"  Unmatched: {addonsState.UnmatchedLiveSlotCount}");
            builder.AppendLine($"  Old DLMM flags: {addonsState.StateOnlyModCount}");

            builder.AppendLine();
            builder.AppendLine("Live addon VPKs:");
            foreach (var line in GetLiveAddonVpkLines(gamePath))
                builder.AppendLine($"  {line}");

            builder.AppendLine();
            builder.AppendLine("Diagnostic cards:");
            foreach (var item in addonsState.Diagnostics.OrderBy(item => item.SortRank).ThenBy(item => item.ModName))
            {
                builder.AppendLine($"  [{item.Status}] {item.ModName}");
                if (!string.IsNullOrWhiteSpace(item.LiveSlot))
                    builder.AppendLine($"    Live: {item.LiveSlot}");
                if (!string.IsNullOrWhiteSpace(item.Evidence))
                    builder.AppendLine($"    Evidence: {item.Evidence}");
                if (!string.IsNullOrWhiteSpace(item.Detail))
                    builder.AppendLine($"    Detail: {item.Detail}");
                if (!string.IsNullOrWhiteSpace(item.RemoteId))
                    builder.AppendLine($"    Remote ID: {item.RemoteId}");
            }

            builder.AppendLine();
            builder.AppendLine("Current loadout:");
            if (loadout.Count == 0)
            {
                builder.AppendLine("  (none)");
            }
            else
            {
                foreach (var pick in loadout.OrderBy(pick => pick.Hero).ThenBy(pick => pick.ModName))
                    builder.AppendLine($"  {pick.HeroDisplay}: {pick.ModName} ({pick.RemoteId})");
            }

            builder.AppendLine();
            builder.AppendLine("Enabled mods by app state:");
            foreach (var mod in mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Hero).ThenBy(mod => mod.Name))
            {
                var group = !string.IsNullOrWhiteSpace(mod.Folder)
                    ? $"folder:{mod.Folder}"
                    : $"hero:{mod.Hero}";
                builder.AppendLine($"  {mod.Name} ({mod.RemoteId}) | {group} | randomiser={mod.IncludedInRandomizer} | slots={string.Join(", ", mod.ActiveVpkSlots)}");
            }

            if (exception is not null)
            {
                builder.AppendLine();
                builder.AppendLine("Exception:");
                builder.AppendLine(exception.ToString());
            }

            builder.AppendLine();
            builder.AppendLine("Recent DLMM log lines:");
            foreach (var line in ReadDlmmLogTail(120))
                builder.AppendLine($"  {line}");

            return builder.ToString();
        }

        private static IEnumerable<string> GetLiveAddonVpkLines(string gamePath)
        {
            var addonsPath = string.IsNullOrWhiteSpace(gamePath)
                ? ""
                : Path.Combine(gamePath, "game", "citadel", "addons");
            if (string.IsNullOrWhiteSpace(addonsPath) || !Directory.Exists(addonsPath))
                return ["(addons folder not found)"];

            try
            {
                return Directory
                    .EnumerateFiles(addonsPath, "*.vpk", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .Where(file => !System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^\d+_", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    .OrderBy(file => file.Name)
                    .Select(file => $"{file.Name} | {file.Length:N0} bytes | {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}")
                    .DefaultIfEmpty("(none)")
                    .ToList();
            }
            catch (IOException ex)
            {
                return [$"(could not read addons folder: {ex.Message})"];
            }
            catch (UnauthorizedAccessException ex)
            {
                return [$"(could not read addons folder: {ex.Message})"];
            }
        }

        private static IEnumerable<string> ReadDlmmLogTail(int maxLines)
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dev.stormix.deadlock-mod-manager",
                "logs",
                "deadlock-mod-manager.log");
            if (!File.Exists(logPath))
                return ["(DLMM log not found)"];

            try
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = new Queue<string>();
                while (reader.ReadLine() is { } line)
                {
                    lines.Enqueue(line);
                    while (lines.Count > maxLines)
                        lines.Dequeue();
                }

                return lines.ToList();
            }
            catch (IOException ex)
            {
                return [$"(could not read DLMM log: {ex.Message})"];
            }
            catch (UnauthorizedAccessException ex)
            {
                return [$"(could not read DLMM log: {ex.Message})"];
            }
        }
    }
}
