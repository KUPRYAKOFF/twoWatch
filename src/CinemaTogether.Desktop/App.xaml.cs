using System.Windows;

namespace CinemaTogether.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Cinema Together", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
