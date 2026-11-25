using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApexHighlighter.Core;

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
            catch (ObsAuthException ex) { LastError = ex.Message; SetState(ObsState.AuthFailed); return; }
            catch (Exception ex) { LastError = ex.Message; }
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

    /// <summary>Connects once, asks OBS for its version, and disconnects. For the settings window's "Test connection" button.</summary>
    public static async Task<string> TestAsync(string host, int port, string? password, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
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
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                try { await ws.ConnectAsync(new Uri($"ws://{host}:{port}"), connectTimeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"ws://{host}:{port} did not answer."); }
            }

            var hello = await ReceiveAsync(ws, ct) ?? throw new InvalidOperationException("OBS closed the connection.");
            var d = hello["d"]!;
            var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = events };
            if (d["authentication"] is { } auth)
            {
                if (password == null) throw new ObsAuthException("OBS requires a password.");
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

public sealed class ObsAuthException(string message) : Exception(message);
