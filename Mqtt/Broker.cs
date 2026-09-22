using System.Net;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace Catopumx.Mqtt;

/// <summary>
/// Wraps an embedded MQTTnet broker plus the two ways the rest of Catopumx
/// talks to it in-process, without opening a loopback network connection to
/// itself:
///
/// - Inbound: <see cref="MqttServer.InterceptingPublishAsync"/> fires for
///   every message any client (or the bridge/alerts) publishes — this is
///   the single tap the ingestion pipeline subscribes to.
/// - Outbound: <see cref="MqttServer.InjectApplicationMessageAsync"/> injects
///   a message as if a client published it, so it is delivered to every
///   subscriber (including the ingestion tap itself) through the broker's
///   normal routing.
/// </summary>
public static class Broker
{
    /// <param name="auth">
    /// Single username/password pair required from MQTT clients, or null to
    /// accept unauthenticated connections. Unauthenticated is only
    /// appropriate on a trusted network or during local development: the
    /// broker defaults to listening on 0.0.0.0, so an unauthenticated broker
    /// is reachable from anywhere that can route to it.
    /// </param>
    public static MqttServer Create(IPEndPoint listenAddress, (string User, string Password)? auth, ILogger logger)
    {
        var optionsBuilder = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(listenAddress.Address)
            .WithDefaultEndpointPort(listenAddress.Port)
            .WithMaxPendingMessagesPerClient(200)
            .WithDefaultCommunicationTimeout(TimeSpan.FromSeconds(60));

        var mqttFactory = new MqttFactory();
        var server = mqttFactory.CreateMqttServer(optionsBuilder.Build());

        if (auth is { } credentials)
        {
            server.ValidatingConnectionAsync += args =>
            {
                if (args.UserName != credentials.User || args.Password != credentials.Password)
                {
                    args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                }
                return Task.CompletedTask;
            };
        }
        else
        {
            logger.LogWarning(
                "MQTT_USERNAME/MQTT_PASSWORD not set: the embedded broker will accept " +
                "unauthenticated connections. Fine for local development on a trusted " +
                "network; set both before exposing MQTT_LISTEN_ADDR beyond localhost.");
        }

        return server;
    }

    /// <summary>Injects a message into the broker as if a client published it.</summary>
    public static Task PublishAsync(this MqttServer server, string topic, string payload) =>
        server.InjectApplicationMessage(new InjectedMqttApplicationMessage(
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(System.Text.Encoding.UTF8.GetBytes(payload))
                .Build()));
}
