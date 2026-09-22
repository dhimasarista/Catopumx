using System.Net;
using Catopumx;
using Microsoft.Data.Sqlite;
using MQTTnet.Server;

LoadDotEnv(".env");

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss ";
});

builder.Services.AddHttpClient<AlertDispatcher>();
builder.Services.AddSingleton<StateCache>();
builder.Services.AddSingleton<EventBus>();

var app = builder.Build();
var logger = app.Logger;

logger.LogInformation("Starting Catopumx - All-in-One IIoT Realtime Hub");

var appConfig = LoadAppConfig("catopumx.toml", logger);

var vault = await ConnectVaultAsync(logger);
var alertEngine = new AlertEngine(appConfig.Alerts);
var alertDispatcher = app.Services.GetRequiredService<AlertDispatcher>();
var cache = app.Services.GetRequiredService<StateCache>();
var bus = app.Services.GetRequiredService<EventBus>();

logger.LogInformation(
    "Configuration loaded: {ModbusDevices} Modbus device(s), {AlertRules} alert rule(s)",
    appConfig.Modbus.Count, alertEngine.Count);

var ingest = new Ingest(
    cache,
    bus,
    vault,
    alertEngine,
    alertDispatcher,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<Ingest>());

// --- MQTT broker bootstrap -------------------------------------------------

var mqttListen = ParseSocketAddress(
    Environment.GetEnvironmentVariable("MQTT_LISTEN_ADDR") ?? "0.0.0.0:1883");

var mqttAuth = BuildMqttAuth(logger);

var mqttServer = Broker.Create(mqttListen, mqttAuth, logger);

mqttServer.InterceptingPublishAsync += async args =>
{
    var segment = args.ApplicationMessage.PayloadSegment;
    var payload = new ReadOnlyMemory<byte>(segment.Array ?? [], segment.Offset, segment.Count);
    await ingest.HandleAsync(args.ApplicationMessage.Topic, payload, mqttServer);
};

await mqttServer.StartAsync();

var cts = new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(() => cts.Cancel());

foreach (var device in appConfig.Modbus)
{
    _ = ModbusBridge.RunAsync(device, mqttServer, logger, cts.Token);
}

// --- HTTP broadcaster --------------------------------------------------

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    database = vault is not null ? "Connected" : "No-DB Mode",
    modbus_devices = appConfig.Modbus.Count,
    alert_rules = alertEngine.Count,
}));

app.MapGet("/api/state", () => Results.Ok(cache.Snapshot()));

app.MapGet("/api/state/{*topic}", (string topic) =>
{
    var value = cache.Get(topic);
    return value is not null ? Results.Ok(value) : Results.NotFound(new { error = "topic not found" });
});

/// <summary>
/// Streams every deduplicated, ingested message as Server-Sent Events. A
/// slow subscriber that falls behind the bus's per-subscriber buffer sees
/// its oldest missed events silently dropped rather than the connection
/// killed.
/// </summary>
app.MapGet("/api/stream", async (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.ContentType = "text/event-stream";

    var (id, reader) = bus.Subscribe();
    try
    {
        await foreach (var evt in reader.ReadAllAsync(context.RequestAborted))
        {
            await context.Response.WriteAsync($"data: {evt}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected; nothing to do.
    }
    finally
    {
        bus.Unsubscribe(id);
    }
});

var httpListen = Environment.GetEnvironmentVariable("HTTP_LISTEN_ADDR") ?? "0.0.0.0:3000";
logger.LogInformation("Broadcaster listening on http://{Addr}", httpListen);
app.Urls.Add($"http://{httpListen}");

await app.RunAsync();

// --- helpers -----------------------------------------------------------

static void LoadDotEnv(string path)
{
    if (!File.Exists(path))
    {
        return;
    }

    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            continue;
        }

        var separatorIndex = trimmed.IndexOf('=');
        if (separatorIndex < 0)
        {
            continue;
        }

        var key = trimmed[..separatorIndex].Trim();
        var value = trimmed[(separatorIndex + 1)..].Trim().Trim('"');
        if (Environment.GetEnvironmentVariable(key) is null)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

static AppConfig LoadAppConfig(string path, ILogger logger)
{
    try
    {
        return AppConfig.Load(path);
    }
    catch (Exception e)
    {
        logger.LogWarning(e, "Failed to load {Path}; continuing with no Modbus devices or alert rules", path);
        return new AppConfig();
    }
}

static IPEndPoint ParseSocketAddress(string raw)
{
    var lastColon = raw.LastIndexOf(':');
    var host = raw[..lastColon];
    var port = int.Parse(raw[(lastColon + 1)..]);
    var address = host is "0.0.0.0" or "" ? IPAddress.Any : IPAddress.Parse(host);
    return new IPEndPoint(address, port);
}

static (string User, string Password)? BuildMqttAuth(ILogger logger)
{
    var user = Environment.GetEnvironmentVariable("MQTT_USERNAME");
    var pass = Environment.GetEnvironmentVariable("MQTT_PASSWORD");
    return !string.IsNullOrEmpty(user) && pass is not null ? (user, pass) : null;
}

/// <summary>
/// Connects to the vault and ensures its schema exists. Any failure along
/// the way (missing URL, unrecognized scheme, connection error, schema
/// error) is logged and treated as "run in No-DB mode" rather than a fatal
/// error, matching the original's broadcast-only fallback behavior.
/// </summary>
static async Task<VaultConnection?> ConnectVaultAsync(ILogger logger)
{
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

    if (string.IsNullOrEmpty(databaseUrl))
    {
        logger.LogWarning("DATABASE_URL not found in environment. Running in No-DB Mode (memory/broadcast only).");
        return null;
    }

    var backend = BackendExtensions.Detect(databaseUrl);
    if (backend is null)
    {
        logger.LogWarning("DATABASE_URL scheme not recognized. Running in No-DB Mode.");
        return null;
    }

    var connectionString = ToProviderConnectionString(backend.Value, databaseUrl);

    try
    {
        await using var connection = backend.Value.CreateConnection(connectionString);
        await connection.OpenAsync();
        await backend.Value.EnsureSchemaAsync(connection);
    }
    catch (Exception e)
    {
        logger.LogWarning(e, "Database connection or schema setup failed. Falling back to No-DB Mode.");
        return null;
    }

    logger.LogInformation("Vault connected and schema ensured: {Backend}", backend);
    return new VaultConnection(backend.Value, connectionString);
}

/// <summary>
/// The provider ADO.NET clients (Npgsql/MySqlConnector/Microsoft.Data.Sqlite)
/// each expect their own connection-string dialect rather than a single
/// portable URL, unlike sqlx's DATABASE_URL. Postgres/MySQL URLs pass
/// straight through to Npgsql/MySqlConnector (both accept a plain
/// "scheme://user:pass@host/db"-style URL); sqlite: URLs are translated to a
/// bare file-path connection string.
/// </summary>
static string ToProviderConnectionString(Backend backend, string databaseUrl) => backend switch
{
    Backend.Sqlite => new SqliteConnectionStringBuilder
    {
        DataSource = databaseUrl["sqlite:".Length..].TrimStart('/'),
    }.ToString(),
    _ => databaseUrl,
};
