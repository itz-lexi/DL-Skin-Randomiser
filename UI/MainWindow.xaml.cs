using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using DL_Skin_Randomiser.Services;

namespace DL_Skin_Randomiser
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            var mods = ModScanner.Scan(@"D:\SteamLibrary\steamapps\common\Deadlock\game\citadel\addons");
            ModsList.ItemsSource = mods;

            foreach (var mod in mods)
            {
                Console.WriteLine($"{mod.Name} -> {mod.Hero}");
            }
        }
    }
}