using System.Diagnostics;
using System.IO;

namespace DL_Skin_Randomiser.Services
{
    public static class GameLaunchService
    {
        private const string DeadlockSteamUri = "steam://rungameid/1422450";

        public static void Launch(string gamePath)
        {
            var executablePath = FindExecutable(gamePath);
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? ""
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = DeadlockSteamUri,
                UseShellExecute = true
            });
        }

        private static string FindExecutable(string gamePath)
        {
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                return "";

            var candidates = new[]
            {
                Path.Combine(gamePath, "game", "bin", "win64", "deadlock.exe"),
                Path.Combine(gamePath, "game", "bin", "win64", "deadlock_win64.exe"),
                Path.Combine(gamePath, "deadlock.exe")
            };

            return candidates.FirstOrDefault(File.Exists) ?? "";
        }
    }
}
