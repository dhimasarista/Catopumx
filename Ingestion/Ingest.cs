using System.Data.Common;
using System.Text;
using System.Text.Json;
using Catopumx.Alerting;
using Catopumx.Storage;
using Microsoft.Extensions.Logging;
using MQTTnet.Server;

namespace Catopumx.Ingestion;

/// <summary>
/// The core pipeline: every MQTT message the broker intercepts is passed to
/// <see cref="HandleAsync"/>, which runs it through dedup (via the state
/// cache), then fans out to the SSE broadcaster, the vault, and the alert
/// engine. A payload identical to the last one seen for its topic
/// short-circuits here and never reaches any of those three, which is what
/// makes persistence and alerting idempotent.
/// </summary>
public sealed class Ingest(
    StateCache cache,
    EventBus bus,
    VaultConnection? vault,
    AlertEngine alertEngine,
    AlertDispatcher alertDispatcher,
    ILogger<Ingest> logger)
{
    /// <summary>
    /// Matches the vault's telemetry_latest.topic VARCHAR(255) column on
    /// MySQL (Postgres/SQLite use unbounded TEXT, but one shared limit is
    /// simplest). A topic longer than this is rejected rather than
    /// truncated, so it never silently merges with a different topic
    /// sharing a prefix.
    /// </summary>
    private const int MaxTopicLen = 255;

    public async Task HandleAsync(string topic, ReadOnlyMemory<byte> payloadBytes, MqttServer mqttServer)
    {
        if (topic.Length > MaxTopicLen)
        {
            logger.LogWarning("Dropping message: topic exceeds max length ({Len} > {Limit})", topic.Length, MaxTopicLen);
            return;
        }

        var payloadStr = Encoding.UTF8.GetString(payloadBytes.Span);

        JsonElement value;
        try
        {
            using var doc = JsonDocument.Parse(payloadStr);
            value = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            value = JsonSerializer.SerializeToElement(payloadStr);
        }

        if (cache.Update(topic, payloadStr, value) == UpdateOutcome.Unchanged)
        {
            return;
        }

        var @event = JsonSerializer.Serialize(new
        {
            topic,
            payload = value,
            ts = DateTimeOffset.UtcNow.ToString("O"),
        });
        // No subscribers is not an error: it just means no dashboard is
        // currently connected to /api/stream.
        bus.Publish(@event);

        if (vault is not null)
        {
            try
            {
                await using DbConnection connection = vault.Backend.CreateConnection(vault.ConnectionString);
                await connection.OpenAsync();
                await vault.Backend.UpsertLatestAsync(connection, topic, payloadStr);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to persist telemetry to the vault for topic {Topic}", topic);
            }
        }

        foreach (var triggered in alertEngine.Evaluate(topic, value))
        {
            await alertDispatcher.DispatchAsync(triggered, mqttServer, CancellationToken.None);
        }
    }
}
