using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DL_Skin_Randomiser.Models
{
    public class Mod
    {
        public string Name { get; set; } = "";
        public string Hero { get; set; } = "";
        public string Path { get; set; } = "";
        public bool Enabled { get; set; }
    }
}
