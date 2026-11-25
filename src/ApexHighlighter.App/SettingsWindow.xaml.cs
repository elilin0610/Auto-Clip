using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        UiLanguageBox.ItemsSource = Loc.Languages;
        UiLanguageBox.SelectedItem = Loc.Languages.First(l => l.Code == Loc.Current);

        var s = App.Settings;
        ObsEnabledBox.IsChecked = s.ObsEnabled;
        ObsHostBox.Text = s.ObsHost;
        ObsPortBox.Text = s.ObsPort.ToString();
        ObsPasswordBox.Password = s.GetObsPassword() ?? "";
        Closing += (_, _) => SaveObs();
    }
    
    private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageBox.SelectedItem is not Loc.Language lang || lang.Code == Loc.Current) return;
        Loc.Set(lang.Code);
        App.Settings.UiLanguage = lang.Code;
        App.Settings.Save();
    }

    private (string Host, int Port) ObsTarget() =>
        (string.IsNullOrWhiteSpace(ObsHostBox.Text) ? "localhost" : ObsHostBox.Text.Trim(),
         int.TryParse(ObsPortBox.Text.Trim(), out var p) && p is > 0 and < 65536 ? p : 4455);

    private void SaveObs()
    {
        var s = App.Settings;
        var (host, port) = ObsTarget();
        s.ObsEnabled = ObsEnabledBox.IsChecked == true;
        s.ObsHost = host;
        s.ObsPort = port;
        // Only re-encrypt when the password actually changed, so the stored value (and the main window's "changed?" check) stays stable.
        if (ObsPasswordBox.Password != (s.GetObsPassword() ?? "")) s.SetObsPassword(ObsPasswordBox.Password);
        s.Save();
    }

    private async void ObsTest_Click(object sender, RoutedEventArgs e)
    {
        var (host, port) = ObsTarget();
        ObsTestButton.IsEnabled = false;
        ObsTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAE));
        ObsTestResult.Text = Loc.T("obs.testing");
        try
        {
            var version = await ObsWatcher.TestAsync(host, port, ObsPasswordBox.Password);
            ObsTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xD1, 0x8B));
            ObsTestResult.Text = Loc.F("obs.ok", version);
        }
        catch (Exception ex)
        {
            ObsTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
            ObsTestResult.Text = ex is ObsAuthException ? Loc.T("obs.authFail") : Loc.F("obs.fail", ex.Message);
        }
        finally { ObsTestButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
