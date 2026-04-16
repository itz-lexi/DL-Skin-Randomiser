using DL_Skin_Randomiser.Models;

namespace DL_Skin_Randomiser.Services
{
    public static class ModService
    {
        public static List<DlmmMod> LoadMods(string statePath)
        {
            return DlmmStateService
                .Load(statePath)
                .Mods
                .ToList();
        }
    }
}
