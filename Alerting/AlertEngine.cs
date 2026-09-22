using System.Collections.Concurrent;
using System.Text.Json;
using Catopumx.Configuration;

namespace Catopumx.Alerting;

/// <summary>A rule crossing its threshold on a specific topic.</summary>
public sealed record TriggeredAlert(
    string RuleName,
    string Topic,
    double Value,
    double Threshold,
    string? PublishTopic,
    string? WebhookUrl);

/// <summary>
/// Evaluates configured threshold rules against ingested payloads.
///
/// Firing is edge-triggered per (rule, topic): an alert is only produced on
/// the transition into the firing state, not on every message while it
/// remains past the threshold, so a webhook or MQTT alert topic doesn't get
/// flooded for as long as a sensor stays hot.
/// </summary>
public sealed class AlertEngine(IReadOnlyList<AlertRule> rules)
{
    private readonly ConcurrentDictionary<(string Rule, string Topic), bool> _firing = new();

    public int Count => rules.Count;

    public List<TriggeredAlert> Evaluate(string topic, JsonElement payload)
    {
        var triggered = new List<TriggeredAlert>();

        foreach (var rule in rules)
        {
            if (rule.Topic != topic)
            {
                continue;
            }

            if (!payload.TryGetProperty(rule.Field, out var fieldValue) ||
                fieldValue.ValueKind != JsonValueKind.Number ||
                !fieldValue.TryGetDouble(out var value))
            {
                continue;
            }

            var isFiring = rule.Operator.Evaluate(value, rule.Threshold);
            var key = (rule.Name, topic);
            var wasFiring = _firing.GetValueOrDefault(key);
            _firing[key] = isFiring;

            if (isFiring && !wasFiring)
            {
                triggered.Add(new TriggeredAlert(rule.Name, topic, value, rule.Threshold, rule.PublishTopic, rule.WebhookUrl));
            }
        }

        return triggered;
    }
}
