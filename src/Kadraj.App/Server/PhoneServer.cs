using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kadraj.Server;

public sealed record PhoneInfo(string Name, string Address);

/// <summary>
/// Serves the phone page over HTTPS and receives JPEG frames over a WebSocket.
/// One phone streams at a time; a newly connected phone takes over from the previous one.
/// </summary>
public sealed class PhoneServer
{
    public const int HttpsPort = 47801;
    public const int HttpPort = 47800; // only redirects to HTTPS, for people typing the address by hand

    const int MaxMessageBytes = 8 * 1024 * 1024;

    readonly object _gate = new();
    WebApplication? _app;
    Session? _active;
    byte[] _pairingKey = [];

    /// <summary>Raised on a Kestrel thread for every frame from the active phone.</summary>
    public event Action<byte[]>? FrameReceived;

    /// <summary>Raised when a phone connects (with its info) or the active phone leaves (null).</summary>
    public event Action<PhoneInfo?>? PhoneChanged;

    public async Task StartAsync(X509Certificate2 certificate, string pairingKey)
    {
        SetPairingKey(pairingKey);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(HttpsPort, listen => listen.UseHttps(certificate));
            kestrel.ListenAnyIP(HttpPort);
            // One phone streams at a time; this is plenty and stops connection floods early.
            kestrel.Limits.MaxConcurrentConnections = 32;
            kestrel.Limits.MaxConcurrentUpgradedConnections = 4;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
            kestrel.AddServerHeader = false;
        });

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            // Phones on the same network only, even if this PC is reachable from the internet.
            if (!IsLocalNetwork(ctx.Connection.RemoteIpAddress))
            {
                ctx.Abort();
                return;
            }
            if (!ctx.Request.IsHttps)
            {
                // Redirect to the address the phone actually reached, never to the Host header
                // (which the client controls and would make this an open redirect).
                var local = ctx.Connection.LocalIpAddress is { } ip && ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ctx.Connection.LocalIpAddress;
                var host = local?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{local}]" : local?.ToString();
                ctx.Response.Redirect($"https://{host}:{HttpsPort}/{ctx.Request.QueryString}");
                return;
            }
            await next();
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });

        var page = LoadPhonePage();
        var contentPolicy = BuildContentPolicy(page);
        app.MapGet("/", (HttpContext ctx) =>
        {
            var headers = ctx.Response.Headers;
            headers.CacheControl = "no-store";
            headers["Content-Security-Policy"] = contentPolicy.Replace("{host}", ctx.Request.Host.Value);
            headers["Referrer-Policy"] = "no-referrer"; // the URL carries the pairing key
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
            return Results.Content(page, "text/html; charset=utf-8");
        });
        app.Map("/ws", (RequestDelegate)HandleSocketAsync);

        await app.StartAsync();
        _app = app;
    }

    /// <summary>Switches to a new pairing key; phones must scan the new QR code. Connected phones are dropped.</summary>
    public void SetPairingKey(string pairingKey)
    {
        Volatile.Write(ref _pairingKey, Encoding.ASCII.GetBytes(pairingKey));
        Session? active;
        lock (_gate) active = _active;
        _ = active?.KickAsync();
    }

    /// <summary>
    /// Strict CSP for the phone page: only its own inline script and style (pinned by hash), and
    /// WebSocket back to this server. Nothing else may load or run.
    /// </summary>
    static string BuildContentPolicy(string page)
    {
        static string Hash(string page, string tag)
        {
            int start = page.IndexOf($"<{tag}>", StringComparison.Ordinal) + tag.Length + 2;
            int end = page.IndexOf($"</{tag}>", start, StringComparison.Ordinal);
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(page[start..end])));
        }

        return "default-src 'none'; " +
               $"script-src 'sha256-{Hash(page, "script")}'; " +
               $"style-src 'sha256-{Hash(page, "style")}'; " +
               "connect-src 'self' wss://{host}; img-src 'self' data: blob:; media-src 'self' blob: mediastream:; " +
               "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }

    /// <summary>Loopback, private IPv4 ranges, IPv4 link-local, IPv6 link-local and unique-local.</summary>
    static bool IsLocalNetwork(System.Net.IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }

    async Task HandleSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Only our own page may open the socket (a web page elsewhere can't, even with the key).
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !string.Equals(origin, $"https://{ctx.Request.Host.Value}", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

        var key = Encoding.ASCII.GetBytes(ctx.Request.Query["k"].ToString());
        if (!CryptographicOperations.FixedTimeEquals(key, Volatile.Read(ref _pairingKey)))
        {
            // The page shows "scan the QR code again" for this close code.
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "bad-key", CancellationToken.None);
            return;
        }

        var address = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?";
        using var session = new Session(socket, address, ctx.RequestAborted);

        Session? previous;
        lock (_gate)
        {
            previous = _active;
            _active = session;
        }
        if (previous is not null) await previous.KickAsync();

        try
        {
            await ReceiveLoopAsync(session);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // Phone went away (Wi-Fi drop, tab closed, screen locked).
        }
        finally
        {
            bool wasActive;
            lock (_gate)
            {
                wasActive = _active == session;
                if (wasActive) _active = null;
            }
            if (wasActive) PhoneChanged?.Invoke(null);
        }
    }

    async Task ReceiveLoopAsync(Session session)
    {
        var socket = session.Socket;
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, session.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None);
                return;
            }
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (session.Announced && _active == session) FrameReceived?.Invoke(message.ToArray());
            }
            else
            {
                HandleText(session, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }
            message.SetLength(0);
        }
    }

    void HandleText(Session session, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "hello" && !session.Announced)
            {
                var name = root.TryGetProperty("device", out var device) ? device.GetString() : null;
                session.Announced = true;
                if (_active == session)
                    PhoneChanged?.Invoke(new PhoneInfo(string.IsNullOrWhiteSpace(name) ? "Phone" : name[..Math.Min(name.Length, 60)], session.Address));
            }
        }
        catch (JsonException)
        {
            // Ignore malformed control messages.
        }
    }

    static string LoadPhonePage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("wwwroot.index.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    sealed class Session(WebSocket socket, string address, CancellationToken requestAborted) : IDisposable
    {
        readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);

        public WebSocket Socket { get; } = socket;
        public string Address { get; } = address;
        public CancellationToken Token => _cts.Token;
        public volatile bool Announced;

        /// <summary>Tells the phone another device took over, then ends its receive loop.</summary>
        public async Task KickAsync()
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes("""{"type":"replaced"}""");
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
            }
            _cts.Cancel();
        }

        public void Dispose() => _cts.Dispose();
    }
}
