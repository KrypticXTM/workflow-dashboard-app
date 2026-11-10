using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WorkflowDashboard;

/// <summary>
/// v1.0.4: loopback-only listener for the yt-adskip browser extension (extension/yt-adskip).
/// The extension's service worker POSTs JSON {event: alive|ad-start|skipped|ad-end, ...} to
/// http://127.0.0.1:47613/adskip. Raw TcpListener on 127.0.0.1 (no HTTP.sys URL ACL, no admin, not reachable off-box).
/// </summary>
internal static class ExtensionBridge
{
    public const int Port = 47613;
    private static long _lastEventTicks;
    private static TcpListener? _listener;

    /// <summary>Raised on a background thread for every event (event name, short detail).</summary>
    public static event Action<string, string>? EventReceived;

    /// <summary>The extension has reported anything within <paramref name="withinSec"/> (it sends "alive" every 60 s per YouTube tab (v1.3.8)).</summary>
    // v1.1.2: dashboard -> extension commands (play/pause). The extension's service worker keeps one "poll" POST
    // open (held here up to 20 s) while a YouTube tab is reporting; a command answers it at once. A command with
    // no poll waiting rides on the response to the next event POST (held for 4 s, then dropped).
    private static readonly object _cmdLock = new();
    private static TaskCompletionSource<string?>? _pollWaiter;
    private static (string Json, DateTime UntilUtc)? _pendingCmd;

    /// <summary>Queue {cmd, tab} for the extension. Returns true if a poll was waiting (delivered now).</summary>
    public static bool QueueCommand(string cmd, string tab)
    {
        long tabId = long.TryParse(tab, out var t) ? t : -1;
        string json = JsonSerializer.Serialize(new { cmd, tab = tabId });
        lock (_cmdLock)
        {
            if (_pollWaiter != null && _pollWaiter.TrySetResult(json)) { _pollWaiter = null; return true; }
            _pendingCmd = (json, DateTime.UtcNow.AddSeconds(4));
            return false;
        }
    }

    private static string? TakePending()
    {
        lock (_cmdLock)
        {
            var p = _pendingCmd; _pendingCmd = null;
            return p != null && p.Value.UntilUtc > DateTime.UtcNow ? p.Value.Json : null;
        }
    }

    private static async Task<string?> WaitForCommand()
    {
        TaskCompletionSource<string?> tcs;
        lock (_cmdLock)
        {
            var p = _pendingCmd; _pendingCmd = null;
            if (p != null && p.Value.UntilUtc > DateTime.UtcNow) return p.Value.Json;
            _pollWaiter?.TrySetResult(null);   // a newer poll replaces an older one
            tcs = _pollWaiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(20000));
        lock (_cmdLock) { if (ReferenceEquals(_pollWaiter, tcs)) _pollWaiter = null; }
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    public static bool IsActive(double withinSec = 150)
    {
        long t = Interlocked.Read(ref _lastEventTicks);
        return t != 0 && (DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc)).TotalSeconds <= withinSec;
    }

    public static void Start(CancellationToken ct, Action<string> log)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            ct.Register(() => { try { _listener.Stop(); } catch { } });
            log($"ext: listening on 127.0.0.1:{Port}");
        }
        catch (Exception ex) { log($"ext: listener failed on port {Port}: {ex.GetType().Name} {ex.Message}"); return; }
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(ct); } catch { break; }
                _ = Task.Run(() => Handle(c));
            }
        });
    }

    private static async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.ReceiveTimeout = 3000;
                var s = c.GetStream();
                var buf = new byte[8192];
                int n = 0, headerEnd = -1, contentLen = 0;
                var cts = new CancellationTokenSource(3000);
                while (n < buf.Length)
                {
                    int r = await s.ReadAsync(buf.AsMemory(n), cts.Token);
                    if (r <= 0) break;
                    n += r;
                    if (headerEnd < 0)
                    {
                        var txt = Encoding.ASCII.GetString(buf, 0, n);
                        int i = txt.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (i >= 0)
                        {
                            headerEnd = i + 4;
                            foreach (var line in txt[..i].Split("\r\n"))
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    int.TryParse(line[15..].Trim(), out contentLen);
                        }
                    }
                    if (headerEnd >= 0 && n >= headerEnd + contentLen) break;
                }
                string head = Encoding.ASCII.GetString(buf, 0, Math.Max(0, Math.Min(n, 64)));
                bool ok = headerEnd >= 0 && head.StartsWith("POST /adskip", StringComparison.Ordinal);
                string? reply = null;
                if (ok)
                {
                    string body = Encoding.UTF8.GetString(buf, headerEnd, Math.Min(contentLen, n - headerEnd));
                    string ev = "?", detail = "";
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        var r = doc.RootElement;
                        if (r.TryGetProperty("event", out var e)) ev = e.GetString() ?? "?";
                        foreach (var prop in r.EnumerateObject())
                        {
                            if (prop.Name is "event" or "url" or "ts" or "tab" or "active") continue;
                            string val = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
                            if (val.Length > 60) val = val[..60];
                            detail += $"{prop.Name}={val} ";
                            if (detail.Length > 300) break;
                        }
                        if (r.TryGetProperty("active", out var a) && a.ValueKind != JsonValueKind.Null) detail += $"tabActive={a} ";
                        if (r.TryGetProperty("tab", out var t)) detail += $"tab={t} ";
                    }
                    catch { ev = "bad-json"; }
                    if (ev.Length > 24) ev = ev[..24];
                    Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
                    if (ev == "poll") reply = await WaitForCommand();   // not logged: it is the command channel
                    else
                    {
                        try { EventReceived?.Invoke(ev, detail.Trim()); } catch { }
                        reply = TakePending();
                    }
                }
                byte[] resp;
                if (ok && reply != null)
                {
                    var body = Encoding.UTF8.GetBytes(reply);
                    resp = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n")
                        .Concat(body).ToArray();
                }
                else resp = Encoding.ASCII.GetBytes(ok ? "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"
                                                      : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(resp);
            }
            catch { }
        }
    }
}
