// Minimal stand-in for the SteelSeries GG core, just enough for SteelSeriesSonar.exe
// to start, register and stay alive without GG/GGEZ/Engine.
//
// Sonar reads GG's location from env vars (GG_SERVER_ADDRESS, GG_WS_ENDPOINT, ...),
// registers via POST /subApps/register + /subApps/ready, polls GET /healthCheck every
// 5 s (exits after 3 failures) and keeps a websocket open for events.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SonarLite.Core;

public sealed class GgHost : IAsyncDisposable
{
    private readonly int _requestedPort;
    private readonly string? _discoveryFile;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private WebApplication? _app;

    /// <param name="port">0 picks a free port.</param>
    /// <param name="discoveryFile">Optional JSON file updated with GG/Sonar addresses, for external tools.</param>
    public GgHost(int port, ILoggerFactory loggerFactory, string? discoveryFile = null)
    {
        _requestedPort = port;
        _discoveryFile = discoveryFile;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger("FakeGG");
    }

    public string BaseAddress { get; private set; } = "";
    public string WebSocketEndpoint => BaseAddress.Replace("http://", "ws://") + "/ws";
    public string? SonarAddress { get; private set; }
    public bool SonarReady { get; private set; }

    public event Action<string>? SonarRegistered;
    public event Action? SonarReadyChanged;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls($"http://127.0.0.1:{_requestedPort}");
        // ASP.NET's own request logging is noise here; our handlers log through _log.
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseWebSockets();
        MapEndpoints(app);
        await app.StartAsync();
        _app = app;

        // After start this reflects the bound address, including the real port when 0 was requested.
        BaseAddress = app.Urls.First();
        _log.LogInformation("FakeGG listening on {Address}", BaseAddress);
    }

    /// <summary>Asks Sonar to run its own shutdown stack (it also disables its virtual devices cleanly).</summary>
    public Task RequestSonarShutdownAsync() => BroadcastAsync("EVENT_SHUTDOWN", null);

    /// <summary>Forget the registered Sonar instance, e.g. after it exited.</summary>
    public void ResetSonar()
    {
        SonarAddress = null;
        SonarReady = false;
        WriteDiscoveryFile();
    }

    public async Task BroadcastAsync(string eventName, object? data)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { @event = eventName, data }));
        foreach (var ws in _sockets.Values)
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
            return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        if (_discoveryFile != null)
        {
            try { File.Delete(_discoveryFile); } catch (IOException) { }
        }
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/healthCheck", () => Results.Json(new { healthCheck = new { status = true, db_read = true } }));

        app.MapPost("/subApps/register", async (HttpRequest req) =>
        {
            var body = await JsonNode.ParseAsync(req.Body);
            SonarAddress = body?["metadata"]?["webServerAddress"]?.GetValue<string>();
            SonarReady = false;
            _log.LogInformation("Sub-app registered: {Name} at {Address}", body?["name"], SonarAddress);
            WriteDiscoveryFile();
            if (SonarAddress != null)
                SonarRegistered?.Invoke(SonarAddress);
            return Results.Ok(new { });
        });

        app.MapPost("/subApps/ready", async (HttpRequest req) =>
        {
            var body = await JsonNode.ParseAsync(req.Body);
            SonarReady = true;
            _log.LogInformation("Sub-app ready: {Name}", body?["name"]);
            WriteDiscoveryFile();
            SonarReadyChanged?.Invoke();
            return Results.Ok(new { });
        });

        // Same shape as the real GG /subApps, so clients written against GG keep working.
        // Engine is reported as not ready: Sonar then skips hardware (Engine) integration.
        app.MapGet("/subApps", () => Results.Json(new
        {
            subApps = new
            {
                sonar = new
                {
                    name = "sonar",
                    isEnabled = true,
                    isReady = SonarReady,
                    isRunning = SonarAddress != null,
                    metadata = new { webServerAddress = SonarAddress ?? "", encryptedWebServerAddress = "" },
                },
                engine = new
                {
                    name = "engine",
                    isEnabled = false,
                    isReady = false,
                    isRunning = false,
                    metadata = new { encryptedWebServerAddress = "" },
                },
            },
        }));

        app.MapGet("/user", () => Results.Json(new { user = new { loggedIn = false, loginSkipped = true } }));
        app.MapGet("/keyboardShortcuts", () => Results.Json(new { keyboardShortcuts = Array.Empty<object>() }));

        app.MapPost("/_fakegg/shutdown", async () =>
        {
            await RequestSonarShutdownAsync();
            return Results.Ok(new { sent = _sockets.Count });
        });

        app.Map("/ws", HandleWebSocketAsync);

        // Anything else Sonar asks GG for (analytics, overlays, quickset/loadouts, alerts...).
        // Writes are acknowledged, reads report 404, which Sonar treats as "feature unavailable".
        app.MapFallback(async (HttpRequest req) =>
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                string body = req.ContentLength > 0 ? await new StreamReader(req.Body).ReadToEndAsync() : "";
                _log.LogDebug("Unhandled {Method} {Path}{Query} {Body}", req.Method, req.Path, req.QueryString, Truncate(body, 200));
            }
            return HttpMethods.IsGet(req.Method) ? Results.NotFound() : Results.Ok(new { });
        });
    }

    private async Task HandleWebSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        _sockets[id] = ws;
        _log.LogInformation("Websocket connected ({Count} open)", _sockets.Count);
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                if (_log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("WS <- {Message}", Truncate(Encoding.UTF8.GetString(ms.ToArray()), 300));
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            _sockets.TryRemove(id, out _);
            _log.LogInformation("Websocket closed ({Count} open)", _sockets.Count);
        }
    }

    private void WriteDiscoveryFile()
    {
        if (_discoveryFile is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_discoveryFile)!);
        File.WriteAllText(_discoveryFile, JsonSerializer.Serialize(new
        {
            ggAddress = BaseAddress,
            sonarAddress = SonarAddress,
            sonarReady = SonarReady,
        }));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
