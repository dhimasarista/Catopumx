using Tomlyn;
using Tomlyn.Model;

namespace Catopumx.Configuration;

/// <summary>
/// Root of catopumx.toml. Both sections are optional and independent: a
/// missing file, or a file with neither section, just means "no Modbus
/// devices, no alert rules" (both features are opt-in).
/// </summary>
public sealed class AppConfig
{
    public List<ModbusDevice> Modbus { get; init; } = [];
    public List<AlertRule> Alerts { get; init; } = [];

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

        var raw = File.ReadAllText(path);
        var model = Toml.ToModel(raw);
        return FromModel(model);
    }

    internal static AppConfig FromModel(TomlTable model)
    {
        var config = new AppConfig();

        if (model.TryGetValue("modbus", out var modbusRaw) && modbusRaw is TomlTableArray modbusArray)
        {
            foreach (var entry in modbusArray)
            {
                config.Modbus.Add(ModbusDevice.FromTable(entry));
            }
        }

        if (model.TryGetValue("alerts", out var alertsRaw) && alertsRaw is TomlTableArray alertsArray)
        {
            foreach (var entry in alertsArray)
            {
                config.Alerts.Add(AlertRule.FromTable(entry));
            }
        }

        return config;
    }
}

public sealed class ModbusDevice
{
    public required string Name { get; init; }

    /// <summary>Modbus TCP gateway address, e.g. "192.168.1.50:502".</summary>
    public required string Address { get; init; }

    public byte SlaveId { get; init; } = 1;
    public ulong PollIntervalMs { get; init; } = 5_000;
    public List<RegisterMap> Registers { get; init; } = [];

    internal static ModbusDevice FromTable(TomlTable table)
    {
        var device = new ModbusDevice
        {
            Name = (string)table["name"],
            Address = (string)table["address"],
            SlaveId = table.TryGetValue("slave_id", out var slaveId) ? (byte)(long)slaveId : (byte)1,
            PollIntervalMs = table.TryGetValue("poll_interval_ms", out var poll) ? (ulong)(long)poll : 5_000UL,
        };

        // TOML represents a list of tables two ways depending on how the
        // user writes it — nested array-of-tables ([[modbus.registers]],
        // parsed as TomlTableArray) or an inline array of inline tables
        // (registers = [{ ... }], parsed as TomlArray) — both are accepted.
        if (table.TryGetValue("registers", out var registersRaw))
        {
            switch (registersRaw)
            {
                case TomlTableArray tableArray:
                    foreach (var entry in tableArray)
                    {
                        device.Registers.Add(RegisterMap.FromTable(entry));
                    }
                    break;
                case TomlArray array:
                    foreach (var entry in array)
                    {
                        device.Registers.Add(RegisterMap.FromTable((TomlTable)entry!));
                    }
                    break;
            }
        }

        return device;
    }
}

public sealed class RegisterMap
{
    public required string Name { get; init; }

    /// <summary>Holding register start address (function code 0x03).</summary>
    public required ushort Address { get; init; }

    public ushort Quantity { get; init; } = 1;

    /// <summary>MQTT topic this register's reading is published to.</summary>
    public required string Topic { get; init; }

    internal static RegisterMap FromTable(TomlTable table) => new()
    {
        Name = (string)table["name"],
        Address = (ushort)(long)table["address"],
        Quantity = table.TryGetValue("quantity", out var q) ? (ushort)(long)q : (ushort)1,
        Topic = (string)table["topic"],
    };
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

    internal static AlertRule FromTable(TomlTable table) => new()
    {
        Name = (string)table["name"],
        Topic = (string)table["topic"],
        Field = (string)table["field"],
        Operator = ParseOperator((string)table["operator"]),
        Threshold = Convert.ToDouble(table["threshold"]),
        PublishTopic = table.TryGetValue("publish_topic", out var pt) ? (string)pt : null,
        WebhookUrl = table.TryGetValue("webhook_url", out var wh) ? (string)wh : null,
    };

    private static Operator ParseOperator(string raw) => raw switch
    {
        "greater_than" => Operator.GreaterThan,
        "greater_or_equal" => Operator.GreaterOrEqual,
        "less_than" => Operator.LessThan,
        "less_or_equal" => Operator.LessOrEqual,
        "equal" => Operator.Equal,
        _ => throw new FormatException($"unknown alert operator '{raw}'"),
    };
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
