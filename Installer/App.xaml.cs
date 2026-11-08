using System.Windows;

namespace VpilotRelaySetup
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show("An unexpected error occurred:\n\n" + args.Exception.Message, "vPilot Relay Setup",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            base.OnStartup(e);
        }
    }
}
