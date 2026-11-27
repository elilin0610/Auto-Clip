using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApexHighlighter.Core;

// =====================================================================================
// ObsWatcher.cs — 連上 OBS，等「錄影停止」的通知
//
// 怎麼運作：
//   OBS 內建 WebSocket 伺服器（預設 ws://localhost:4455）。連上後：
//     OBS → 我們  Hello（op 0）：附上 salt 和 challenge 兩個亂數
//     我們 → OBS  Identify（op 1）：用密碼 + 亂數算出驗證碼（AuthString），並說要訂閱「輸出」類事件
//     OBS → 我們  Identified（op 2）：驗證通過
//   之後線路一直開著，OBS 有事件就推過來（op 5）。收到 RecordStateChanged = 停止，就觸發 RecordingStopped。
//
// 密碼本身從不在網路上傳送，傳的是雜湊過的驗證碼。
// 這個類別只「聽」，不會送任何會改變 OBS 的指令。
// 斷線會每 10 秒自動重連（OBS 可能比本程式晚開）；密碼錯就停下不再試。
// =====================================================================================

public enum ObsState { Off, Connecting, Connected, Disconnected, AuthFailed }

/// <summary>
/// Talks to OBS through its built-in WebSocket server (obs-websocket 5, OBS 28+).
/// Only listens: it never changes anything in OBS. Raises <see cref="RecordingStopped"/> with the file path when a recording ends.
/// </summary>
public sealed class ObsWatcher : IAsyncDisposable
{
    private const int OutputsEvents = 1 << 6;   // obs-websocket EventSubscription.Outputs
    private const int AuthFailedCode = 4009;    // WebSocketCloseCode.AuthenticationFailed

    private readonly string _host;
    private readonly int _port;
    private readonly string? _password;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ObsState State { get; private set; } = ObsState.Off;
    public string? LastError { get; private set; }

    /// <summary>Raised on a thread-pool thread.</summary>
    public event Action<ObsState>? StateChanged;
    /// <summary>Raised on a thread-pool thread with the finished recording's full path.</summary>
    public event Action<string>? RecordingStopped;

    public ObsWatcher(string host, int port, string? password)
    {
        _host = host; _port = port; _password = string.IsNullOrEmpty(password) ? null : password;
    }

    /// <summary>Connects in the background and keeps reconnecting every 10 s (OBS may start after this app). Stops for good on a wrong password.</summary>
    public void Start() => _loop ??= Task.Run(RunAsync);

    // 背景迴圈：連線 → 一直收訊息 → 斷了就等 10 秒再連
    private async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            SetState(ObsState.Connecting);
            try
            {
                using var ws = await ConnectAsync(_host, _port, _password, OutputsEvents, ct);
                SetState(ObsState.Connected);
                LastError = null;
                while (!ct.IsCancellationRequested)
                {
                    var msg = await ReceiveAsync(ws, ct);
                    if (msg == null) break;
                    if (RecordingPathFromEvent(msg) is { } path) RecordingStopped?.Invoke(path);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ObsAuthException ex) { LastError = FriendlyError(ex, _host, _port); SetState(ObsState.AuthFailed); return; }
            catch (Exception ex) { LastError = FriendlyError(ex, _host, _port); }
            SetState(ObsState.Disconnected);
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
        }
        SetState(ObsState.Off);
    }

    private void SetState(ObsState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>How long to wait for the TCP/WebSocket connection. OBS on the same PC answers in milliseconds.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Whole "Test connection" budget: connect + handshake + GetVersion.</summary>
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// "localhost" is dialled as 127.0.0.1: Windows otherwise tries ::1 first and waits ~2 s for each refused address,
    /// which made a failed test take about 10 s. OBS's WebSocket server listens on IPv4.
    /// </summary>
    public static string DialHost(string host) =>
        string.Equals(host.Trim(), "localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : host.Trim();

    /// <summary>What went wrong, in the interface language, with what to do about it (instead of the raw .NET message).</summary>
    // 把 .NET 的例外訊息換成看得懂的說明：OBS 沒開 / 沒啟用 WebSocket、連接埠上不是 OBS、要密碼、密碼錯。
    public static string FriendlyError(Exception ex, string host, int port) => Classify(ex) switch
    {
        ObsFailure.NotReachable => Loc.F("obs.notReachable", host, port),
        ObsFailure.NotObs => Loc.F("obs.notObs", host, port),
        ObsFailure.NeedsPassword => Loc.T("obs.needPassword"),
        ObsFailure.WrongPassword => Loc.T("obs.authFail"),
        _ => Loc.F("obs.fail", ex.Message),
    };

    public static ObsFailure Classify(Exception ex)
    {
        if (ex is ObsAuthException a) return a.PasswordMissing ? ObsFailure.NeedsPassword : ObsFailure.WrongPassword;
        if (ex is TimeoutException or OperationCanceledException) return ObsFailure.NotReachable;
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException or HttpRequestException) return ObsFailure.NotReachable;
            if (e is WebSocketException w && w.WebSocketErrorCode == WebSocketError.NotAWebSocket) return ObsFailure.NotObs;
        }
        if (ex is WebSocketException) return ObsFailure.NotReachable;
        if (ex is JsonException) return ObsFailure.NotObs;
        return ObsFailure.Other;
    }

    /// <summary>Connects once, asks OBS for its version, and disconnects. For the settings window's "Test connection" button.</summary>
    public static async Task<string> TestAsync(string host, int port, string? password, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        using var ws = await ConnectAsync(host, port, string.IsNullOrEmpty(password) ? null : password, 0, timeout.Token);
        var req = new JsonObject { ["op"] = 6, ["d"] = new JsonObject { ["requestType"] = "GetVersion", ["requestId"] = "v" } };
        await SendAsync(ws, req, timeout.Token);
        while (await ReceiveAsync(ws, timeout.Token) is { } msg)
        {
            if (msg["op"]?.GetValue<int>() == 7)
            {
                var data = msg["d"]?["responseData"];
                await CloseQuietlyAsync(ws);
                return data?["obsVersion"]?.GetValue<string>() ?? "?";
            }
        }
        throw new InvalidOperationException("OBS closed the connection.");
    }

    /// <summary>Hello → Identify (with the obs-websocket 5 challenge/salt auth) → Identified.</summary>
    private static async Task<ClientWebSocket> ConnectAsync(string host, int port, string? password, int events, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectTimeout.CancelAfter(ConnectTimeout);
                try { await ws.ConnectAsync(new Uri($"ws://{DialHost(host)}:{port}"), connectTimeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"ws://{host}:{port} did not answer."); }
            }

            // 握手：Hello → Identify → Identified
            var hello = await ReceiveAsync(ws, ct) ?? throw new InvalidOperationException("OBS closed the connection.");
            var d = hello["d"]!;
            var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = events };
            if (d["authentication"] is { } auth)
            {
                if (password == null) throw new ObsAuthException("OBS requires a password.", passwordMissing: true);
                identify["authentication"] = AuthString(password, auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
            }
            await SendAsync(ws, new JsonObject { ["op"] = 1, ["d"] = identify }, ct);

            var reply = await ReceiveAsync(ws, ct);
            if (reply == null)
            {
                if (ws.CloseStatus == (WebSocketCloseStatus)AuthFailedCode) throw new ObsAuthException("Wrong password.");
                throw new InvalidOperationException($"OBS closed the connection ({ws.CloseStatusDescription}).");
            }
            if (reply["op"]?.GetValue<int>() != 2) throw new InvalidOperationException("Unexpected reply from OBS.");
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary>base64(sha256(base64(sha256(password + salt)) + challenge)), as obs-websocket 5 specifies.</summary>
    public static string AuthString(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>The output path of a "recording stopped" event, or null for anything else.</summary>
    public static string? RecordingPathFromEvent(JsonNode msg)
    {
        if (msg["op"]?.GetValue<int>() != 5) return null;
        var d = msg["d"];
        if (d?["eventType"]?.GetValue<string>() != "RecordStateChanged") return null;
        var data = d["eventData"];
        if (data?["outputState"]?.GetValue<string>() != "OBS_WEBSOCKET_OUTPUT_STOPPED") return null;
        var path = data["outputPath"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static async Task SendAsync(ClientWebSocket ws, JsonNode msg, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

    /// <summary>One whole text message, or null when the server closes the connection.</summary>
    // 一則訊息可能分好幾塊送來，收到 EndOfMessage 才算完整。上限 4 MB 防止異常大的訊息吃光記憶體。
    private static async Task<JsonNode?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Message from OBS is too large.");
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray());
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket ws)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, t.Token);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null) try { await _loop; } catch { }
        _cts.Dispose();
    }
}

public enum ObsFailure { Other, NotReachable, NotObs, NeedsPassword, WrongPassword }

public sealed class ObsAuthException(string message, bool passwordMissing = false) : Exception(message)
{
    public bool PasswordMissing { get; } = passwordMissing;
}
