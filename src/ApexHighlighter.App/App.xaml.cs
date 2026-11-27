using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;

// =====================================================================================
// App.xaml.cs — 程式進入點
//
// 啟動時讀取設定（AppSettings）並套用介面語言，然後開 MainWindow。
// 只允許開一個：已經有一個在跑時，第二次開只會把原本的視窗叫到最前面，然後自己結束。
// 第一次開（還沒選過語言）就跟 Windows 顯示語言：中文 → 繁中、日文 → 日文、其他 → 英文。
// App.xaml 裡放全部視窗共用的樣式（例如深色下拉選單）。
// =====================================================================================

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    // Per Windows user session; held for the app's lifetime (kept in a field so it isn't garbage-collected).
    private const string InstanceMutexName = @"Local\ApexHighlighter.App.SingleInstance";
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another copy is already running: bring it to the front instead of opening a second window.
            _instanceMutex.Dispose();
            _instanceMutex = null;
            ActivateRunningInstance();
            Shutdown();
            return;
        }

        Settings = AppSettings.Load();
        // First run: follow the Windows display language (Chinese → 繁體中文, Japanese → 日本語, else English).
        Loc.Set(Loc.IsSupported(Settings.UiLanguage) ? Settings.UiLanguage! : Loc.FromSystem());
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private static void ActivateRunningInstance()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(self.ProcessName))
        {
            using (p)
            {
                if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
}
