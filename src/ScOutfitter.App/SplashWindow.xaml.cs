using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ScOutfitter.Core;

namespace ScOutfitter.App;

public partial class SplashWindow : Window
{
    private TaskCompletionSource<bool>? _retry;
    private TaskCompletionSource<(bool Erkul, string Link)>? _choice;

    public SplashWindow()
    {
        InitializeComponent();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        VersionText.Text = $"v{version}";
    }

    public async Task<DataStore> LoadAsync(WikiClient client)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        var progress = new Progress<string>(text => StatusText.Text = text);
        return await Task.Run(() => DataStore.LoadAsync(client, progress));
    }

    /// <summary>Show the error and wait for the user: true = retry, false = quit.</summary>
    public Task<bool> ShowErrorAsync(Exception ex)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = ex.InnerException?.Message ?? ex.Message;
        _retry = new TaskCompletionSource<bool>();
        return _retry.Task;
    }

    /// <summary>Data is in: let the user pick the mode. Returns (erkul mode?, pasted link).</summary>
    public Task<(bool Erkul, string Link)> ChooseModeAsync()
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ChoicePanel.Visibility = Visibility.Visible;
        _choice = new TaskCompletionSource<(bool, string)>();
        return _choice.Task;
    }

    private void OnPlanner(object sender, RoutedEventArgs e) => _choice?.TrySetResult((false, ErkulLinkBox.Text.Trim()));

    private void OnErkul(object sender, RoutedEventArgs e) => _choice?.TrySetResult((true, ErkulLinkBox.Text.Trim()));

    private void OnRetry(object sender, RoutedEventArgs e) => _retry?.TrySetResult(true);

    private void OnQuit(object sender, RoutedEventArgs e) => _retry?.TrySetResult(false);

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
