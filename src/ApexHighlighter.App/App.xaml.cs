using System.Windows;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;

// =====================================================================================
// App.xaml.cs — 程式進入點
//
// 啟動時讀取設定（AppSettings）並套用介面語言，然後 WPF 會開 MainWindow。
// 第一次開（還沒選過語言）就跟 Windows 顯示語言：中文 → 繁中、日文 → 日文、其他 → 英文。
// App.xaml 裡放全部視窗共用的樣式（例如深色下拉選單）。
// =====================================================================================

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = AppSettings.Load();
        // First run: follow the Windows display language (Chinese → 繁體中文, Japanese → 日本語, else English).
        Loc.Set(Loc.IsSupported(Settings.UiLanguage) ? Settings.UiLanguage! : Loc.FromSystem());
        base.OnStartup(e);
    }
}
