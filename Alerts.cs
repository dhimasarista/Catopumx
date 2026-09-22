using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MQTTnet.Server;

namespace Catopumx;

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

/// <summary>Dispatches a triggered alert: republish to MQTT and/or POST a webhook.</summary>
public sealed class AlertDispatcher(HttpClient httpClient, ILogger<AlertDispatcher> logger)
{
    public async Task DispatchAsync(TriggeredAlert alert, MqttServer mqttServer, CancellationToken ct)
    {
        logger.LogInformation(
            "Alert triggered: rule={Rule} topic={Topic} value={Value} threshold={Threshold}",
            alert.RuleName, alert.Topic, alert.Value, alert.Threshold);

        var body = JsonSerializer.Serialize(new
        {
            rule = alert.RuleName,
            topic = alert.Topic,
            value = alert.Value,
            threshold = alert.Threshold,
        });

        if (alert.PublishTopic is { } publishTopic)
        {
            try
            {
                await mqttServer.PublishAsync(publishTopic, body);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to republish alert to MQTT topic {Topic}", publishTopic);
            }
        }

        if (alert.WebhookUrl is { } webhookUrl)
        {
            // Fire-and-forget, same as the original's spawn_blocking: a slow
            // or failing webhook must never stall the ingestion pipeline.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    using var response = await httpClient.PostAsync(webhookUrl, content, cts.Token);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Webhook dispatch failed: {Url}", RedactCredentials(webhookUrl));
                }
            }, ct);
        }
    }

    /// <summary>
    /// Strips "user:pass@" userinfo from a URL before it's logged, so
    /// credentials an operator embedded in webhook_url don't end up in
    /// application logs on every failed delivery.
    /// </summary>
    internal static string RedactCredentials(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return url;
        }

        var scheme = url[..(schemeEnd + 3)];
        var rest = url[(schemeEnd + 3)..];
        var at = rest.IndexOf('@');
        return at < 0 ? url : $"{scheme}***@{rest[(at + 1)..]}";
    }
}
