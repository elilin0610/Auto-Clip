using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;

// =====================================================================================
// SettingsWindow.xaml.cs — 設定視窗
//
// 介面語言（選了立刻生效）、精華挑選（最多幾段、最低分數、最長秒數）、OBS 開關 / 位址 / 連接埠 / 密碼、「測試連線」按鈕。
// 關閉視窗時存檔；密碼有改才重新加密。
// =====================================================================================

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        UiLanguageBox.ItemsSource = Loc.Languages;
        UiLanguageBox.SelectedItem = Loc.Languages.First(l => l.Code == Loc.Current);

        var s = App.Settings;
        ObsEnabledBox.IsChecked = s.ObsEnabled;
        ObsHostBox.Text = s.ObsHost;
        ObsHostBox_TextChanged(ObsHostBox, null);
        ObsPortBox.Text = s.ObsPort.ToString();
        ObsPasswordBox.Password = s.GetObsPassword() ?? "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        MaxClipsBox.Text = s.MaxCandidates.ToString(inv);
        MinScoreBox.Text = s.MinScore.ToString("0.##", inv);
        MaxLenBox.Text = s.MaxClipLength.ToString("0", inv);
        Closing += (_, _) => { SaveScan(); SaveObs(); };
    }
    
    private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageBox.SelectedItem is not Loc.Language lang || lang.Code == Loc.Current) return;
        Loc.Set(lang.Code);
        App.Settings.UiLanguage = lang.Code;
        App.Settings.Save();
    }

    // 位址不是本機（localhost / 127.0.0.1 / ::1）就顯示「連線沒有加密」的提醒
    private void ObsHostBox_TextChanged(object sender, TextChangedEventArgs? e) =>
        ObsHostWarning.Visibility = ObsWatcher.IsLoopbackHost(ObsHostBox.Text) ? Visibility.Collapsed : Visibility.Visible;

    private (string Host, int Port) ObsTarget() =>
        (string.IsNullOrWhiteSpace(ObsHostBox.Text) ? "localhost" : ObsHostBox.Text.Trim(),
         int.TryParse(ObsPortBox.Text.Trim(), out var p) && p is > 0 and < 65536 ? p : 4455);

    /// <summary>Highlight selection settings. Anything that doesn't parse or is out of range keeps the previous value.</summary>
    private void SaveScan()
    {
        var s = App.Settings;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var num = System.Globalization.NumberStyles.Float;
        if (int.TryParse(MaxClipsBox.Text.Trim(), out var n) && n is >= 1 and <= 50) s.MaxCandidates = n;
        if (double.TryParse(MinScoreBox.Text.Trim(), num, inv, out var m) && m is >= 0 and <= 50) s.MinScore = m;
        if (double.TryParse(MaxLenBox.Text.Trim(), num, inv, out var l) && (l == 0 || l is >= 15 and <= 3600)) s.MaxClipLength = l;
    }

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
            ObsTestResult.Text = ObsWatcher.FriendlyError(ex, host, port);
        }
        finally { ObsTestButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
