using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Catopumx.Ingestion;

/// <summary>
/// The most recently seen value for a topic, kept in memory so dashboards
/// can read current state instantly without touching the database.
/// </summary>
public sealed class CachedValue
{
    public required JsonElement Payload { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore]
    internal int Hash { get; init; }
}

public enum UpdateOutcome
{
    Changed,
    Unchanged,
}

/// <summary>
/// In-memory latest-value store, keyed by MQTT topic.
///
/// <see cref="Update"/> also acts as the dedup gate for the rest of the
/// pipeline: a payload identical (byte-for-byte) to the last one seen for its
/// topic is reported as <see cref="UpdateOutcome.Unchanged"/> so the caller
/// can skip broadcasting, persisting, and re-evaluating alerts for it.
/// </summary>
public sealed class StateCache
{
    /// <summary>
    /// Hard ceiling on distinct topics tracked at once. Without this, a
    /// client that can publish (the embedded broker has no auth unless
    /// MQTT_USERNAME / MQTT_PASSWORD are set) could grow this cache without
    /// bound by publishing to a stream of unique topics. Once the limit is
    /// reached, messages for topics not already tracked are dropped from the
    /// whole pipeline (cache, vault, SSE, alerts) rather than just left
    /// uncached — this is a circuit breaker, not graceful degradation. Real
    /// IIoT deployments have nowhere near this many distinct topics.
    /// </summary>
    private const int MaxTrackedTopics = 10_000;

    private readonly ConcurrentDictionary<string, CachedValue> _values = new();

    public UpdateOutcome Update(string topic, string rawPayload, JsonElement payload)
    {
        var hash = string.GetHashCode(rawPayload, StringComparison.Ordinal);

        if (_values.TryGetValue(topic, out var existing))
        {
            if (existing.Hash == hash)
            {
                return UpdateOutcome.Unchanged;
            }
        }
        else if (_values.Count >= MaxTrackedTopics)
        {
            return UpdateOutcome.Unchanged;
        }

        _values[topic] = new CachedValue
        {
            Payload = payload,
            UpdatedAt = DateTimeOffset.UtcNow,
            Hash = hash,
        };
        return UpdateOutcome.Changed;
    }

    public CachedValue? Get(string topic) => _values.TryGetValue(topic, out var value) ? value : null;

    public IReadOnlyDictionary<string, CachedValue> Snapshot() =>
        new Dictionary<string, CachedValue>(_values);
}
