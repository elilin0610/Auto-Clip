using System.Windows;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;

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
