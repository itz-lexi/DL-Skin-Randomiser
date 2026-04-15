using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class ModScanner
    {
        private static readonly string[] Heroes =
        {
        "holiday",
        "warden",
        "dynamo"
        // add more later
    };

        public static List<Mod> Scan(string modsPath)
        {
            var mods = new List<Mod>();

            if (!Directory.Exists(modsPath))
                return mods;

            foreach (var dir in Directory.GetDirectories(modsPath))
            {
                var folderName = Path.GetFileName(dir).ToLower();

                var hero = Heroes.FirstOrDefault(h => folderName.Contains(h));

                if (hero != null)
                {
                    mods.Add(new Mod
                    {
                        Name = Path.GetFileName(dir),
                        Hero = hero,
                        Path = dir,
                        Enabled = false
                    });
                }
            }

            return mods;
        }
    }
}
