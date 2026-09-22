using System.Text.Json;
using Catopumx.Mqtt;
using Microsoft.Extensions.Logging;
using MQTTnet.Server;

namespace Catopumx.Alerting;

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
            // Fire-and-forget: a slow or failing webhook must never stall
            // the ingestion pipeline.
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
