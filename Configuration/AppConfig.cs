using System.Text.Json;
using System.Text.Json.Serialization;

namespace Catopumx.Configuration;

/// <summary>
/// Root of catopumx.json. Both sections are optional and independent: a
/// missing file, or a file with neither section, just means "no Modbus
/// devices, no alert rules" (both features are opt-in).
/// </summary>
public sealed class AppConfig
{
    public List<ModbusDevice> Modbus { get; init; } = [];
    public List<AlertRule> Alerts { get; init; } = [];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Loads config from <paramref name="path"/>. A missing file is not an
    /// error: it means "no Modbus devices, no alert rules".
    /// </summary>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        return FromJson(File.ReadAllText(path));
    }

    internal static AppConfig FromJson(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions) ?? new AppConfig();
}

public sealed class ModbusDevice
{
    public required string Name { get; init; }

    /// <summary>Modbus TCP gateway address, e.g. "192.168.1.50:502".</summary>
    public required string Address { get; init; }

    public byte SlaveId { get; init; } = 1;
    public ulong PollIntervalMs { get; init; } = 5_000;
    public List<RegisterMap> Registers { get; init; } = [];
}

public sealed class RegisterMap
{
    public required string Name { get; init; }

    /// <summary>Holding register start address (function code 0x03).</summary>
    public required ushort Address { get; init; }

    public ushort Quantity { get; init; } = 1;

    /// <summary>MQTT topic this register's reading is published to.</summary>
    public required string Topic { get; init; }
}

public sealed class AlertRule
{
    public required string Name { get; init; }

    /// <summary>Exact MQTT topic to watch. Wildcards ("+", "#") are not supported yet.</summary>
    public required string Topic { get; init; }

    /// <summary>Top-level numeric field in the topic's JSON payload to evaluate.</summary>
    public required string Field { get; init; }

    public required Operator Operator { get; init; }
    public required double Threshold { get; init; }

    /// <summary>Re-publish a JSON alert to this MQTT topic when the rule fires.</summary>
    public string? PublishTopic { get; init; }

    /// <summary>POST a JSON alert to this URL when the rule fires.</summary>
    public string? WebhookUrl { get; init; }
}

public enum Operator
{
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Equal,
}

public static class OperatorExtensions
{
    public static bool Evaluate(this Operator op, double value, double threshold) => op switch
    {
        Operator.GreaterThan => value > threshold,
        Operator.GreaterOrEqual => value >= threshold,
        Operator.LessThan => value < threshold,
        Operator.LessOrEqual => value <= threshold,
        Operator.Equal => Math.Abs(value - threshold) < double.Epsilon,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
}
