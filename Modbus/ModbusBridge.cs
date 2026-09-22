using System.Net;
using System.Text.Json;
using Catopumx.Configuration;
using Catopumx.Mqtt;
using FluentModbus;
using Microsoft.Extensions.Logging;
using MQTTnet.Server;

namespace Catopumx.Modbus;

/// <summary>
/// Polls one Modbus TCP device on a fixed interval and republishes each
/// configured register's reading as an MQTT message, so downstream
/// ingestion (dedup, cache, vault, alerts, SSE) never has to know a device
/// isn't speaking MQTT natively.
///
/// The connection is re-established every poll cycle rather than kept alive
/// and reconnected on error. This is simpler and more robust against
/// half-open sockets at the cost of reconnect overhead on each tick; for
/// high-frequency polling this would be worth revisiting.
/// </summary>
public static class ModbusBridge
{
    public static async Task RunAsync(ModbusDevice device, MqttServer mqttServer, ILogger logger, CancellationToken ct)
    {
        if (!IPEndPoint.TryParse(device.Address, out var endpoint))
        {
            logger.LogWarning("Invalid Modbus address '{Address}' for device {Device}; this device will not be polled", device.Address, device.Name);
            return;
        }

        if (device.PollIntervalMs == 0)
        {
            logger.LogWarning("poll_interval_ms must be non-zero for device {Device}; this device will not be polled", device.Name);
            return;
        }

        logger.LogInformation("Starting Modbus polling loop for {Device} at {Endpoint}", device.Name, endpoint);
        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(device.PollIntervalMs));

        while (await ticker.WaitForNextTickAsync(ct))
        {
            await PollOnceAsync(device, endpoint, mqttServer, logger);
        }
    }

    private static async Task PollOnceAsync(ModbusDevice device, IPEndPoint endpoint, MqttServer mqttServer, ILogger logger)
    {
        var client = new ModbusTcpClient();

        try
        {
            await Task.Run(() => client.Connect(endpoint, ModbusEndianness.BigEndian));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Modbus TCP connect failed for device {Device}", device.Name);
            return;
        }

        try
        {
            foreach (var register in device.Registers)
            {
                try
                {
                    var values = await Task.Run(() =>
                        client.ReadHoldingRegisters<ushort>(device.SlaveId, register.Address, register.Quantity).ToArray());

                    var payload = JsonSerializer.Serialize(new
                    {
                        device = device.Name,
                        register = register.Name,
                        value = values.Length > 0 ? values[0] : (ushort?)null,
                        values,
                        ts = DateTimeOffset.UtcNow.ToString("O"),
                    });

                    await mqttServer.PublishAsync(register.Topic, payload);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Modbus read failed for device {Device} register {Register}", device.Name, register.Name);
                }
            }
        }
        finally
        {
            try
            {
                client.Disconnect();
            }
            catch
            {
                // Best-effort: the connection may already be dead.
            }
        }
    }
}
