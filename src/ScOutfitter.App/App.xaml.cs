using System.Globalization;
using System.Windows;
using ScOutfitter.Core;

namespace ScOutfitter.App;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // English UI with English numbers, whatever the Windows locale is
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("en-US")));

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "sc-outfitter - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var splash = new SplashWindow();
        splash.Show();

        var client = new WikiClient();
        while (true)
        {
            try
            {
                DataStore data = await splash.LoadAsync(client);
                var main = new MainWindow(new MainViewModel(data));
                MainWindow = main;
                main.Closed += (_, _) => Shutdown();
                main.Show();
                splash.Close();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                bool retry = await splash.ShowErrorAsync(ex);
                if (!retry)
                {
                    Shutdown();
                    return;
                }
            }
        }
    }
}
