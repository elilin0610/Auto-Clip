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

    // Per Windows user session. The mutex says "already running"; the event lets a second launch ask the first one to
    // come to the front. Works the same whether the app runs as ApexHighlighter.App.exe or as `dotnet ApexHighlighter.App.dll`
    // (process name "dotnet"), because nothing here looks processes up by name.
    // 單一實例：Mutex 判斷是否已在執行，具名事件通知原本那個把視窗叫到前面（不靠程序名稱，所以 exe 或 dotnet 啟動都可以）。
    private const string InstanceMutexName = @"Local\ApexHighlighter.App.SingleInstance";
    private const string ActivateEventName = @"Local\ApexHighlighter.App.Activate";
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateEvent;
    private static RegisteredWaitHandle? _activateWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another copy is already running: ask it to come to the front instead of opening a second window.
            _instanceMutex.Dispose();
            _instanceMutex = null;
            SignalRunningInstance();
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(BringToFront)), null, Timeout.Infinite, executeOnlyOnce: false);

        Settings = AppSettings.Load();
        // First run: follow the Windows display language (Chinese → 繁體中文, Japanese → 日本語, else English).
        Loc.Set(Loc.IsSupported(Settings.UiLanguage) ? Settings.UiLanguage! : Loc.FromSystem());
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>Second launch: let the running copy take the foreground (Windows only allows it when we pass the right on), then wake it.</summary>
    private static void SignalRunningInstance()
    {
        AllowSetForegroundWindow(ASFW_ANY);
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var ev))
                using (ev) ev.Set();
        }
        catch { /* the first copy is shutting down; nothing to bring forward */ }
    }

    /// <summary>First copy, on the UI thread: restore if minimized and bring the main window to the front.</summary>
    private void BringToFront()
    {
        if (MainWindow is not { } w) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        if (!w.IsVisible) w.Show();
        w.Activate();
        // Topmost toggle: a common WPF trick to get past focus-stealing rules when Activate alone only flashes the taskbar.
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    private const int ASFW_ANY = -1;
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
