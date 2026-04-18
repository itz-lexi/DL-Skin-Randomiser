using System.Windows;
using DL_Skin_Randomiser.Services;

namespace DL_Skin_Randomiser
{
    public partial class App
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (UpdateService.IsPortableUpdateCommand(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = await UpdateService.ApplyPortableUpdateFromCommandLineAsync(e.Args);
                Shutdown(exitCode);
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
    }
}
