using Cleario.Services;
using Microsoft.UI.Xaml;
using System;

namespace Cleario
{
    public partial class App : Application
    {
        public static MainWindow? MainAppWindow { get; private set; }

        public App()
        {
            CrashLogger.Initialize();
            CrashLogger.RegisterXamlUnhandledException(this);

            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                CrashLogger.LogMessage("OnLaunched started.");

                MainAppWindow = new MainWindow();
                MainAppWindow.Activate();

                _ = DiscordRichPresenceService.InitializeAsync();

                CrashLogger.LogMessage("Main window activated.");
            }
            catch (Exception ex)
            {
                CrashLogger.LogException(ex, "App.OnLaunched", isFatal: true);
                throw;
            }
        }
    }
}
