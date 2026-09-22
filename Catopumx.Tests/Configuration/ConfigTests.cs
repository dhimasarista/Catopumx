using Catopumx.Configuration;
using Xunit;

namespace Catopumx.Tests.Configuration;

public class ConfigTests
{
    [Fact]
    public void MissingFileYieldsDefaultConfig()
    {
        var config = AppConfig.Load("does-not-exist.json");
        Assert.Empty(config.Modbus);
        Assert.Empty(config.Alerts);
    }

    [Theory]
    [InlineData(10.0, 5.0, true)]
    [InlineData(5.0, 10.0, false)]
    public void GreaterThanEvaluatesCorrectly(double value, double threshold, bool expected) =>
        Assert.Equal(expected, Operator.GreaterThan.Evaluate(value, threshold));

    [Fact]
    public void LessOrEqualEvaluatesCorrectly() =>
        Assert.True(Operator.LessOrEqual.Evaluate(5.0, 5.0));

    [Fact]
    public void EqualEvaluatesCorrectly()
    {
        Assert.True(Operator.Equal.Evaluate(5.0, 5.0));
        Assert.False(Operator.Equal.Evaluate(5.0, 5.1));
    }

    [Fact]
    public void ParsesFullConfigDocument()
    {
        const string json = """
            {
              "modbus": [
                {
                  "name": "line1",
                  "address": "127.0.0.1:502",
                  "registers": [
                    { "name": "temp", "address": 0, "topic": "factory/line1/temp" }
                  ]
                }
              ],
              "alerts": [
                {
                  "name": "overheat",
                  "topic": "factory/line1/temp",
                  "field": "value",
                  "operator": "greaterThan",
                  "threshold": 80.0,
                  "publishTopic": "alerts/line1/overheat"
                }
              ]
            }
            """;

        var config = AppConfig.FromJson(json);

        Assert.Single(config.Modbus);
        Assert.Equal(1, config.Modbus[0].SlaveId);
        Assert.Equal(1, config.Modbus[0].Registers[0].Quantity);
        Assert.Single(config.Alerts);
        Assert.Equal(Operator.GreaterThan, config.Alerts[0].Operator);
    }

    [Fact]
    public void ParsesMultipleRegistersOnOneDevice()
    {
        const string json = """
            {
              "modbus": [
                {
                  "name": "line1-plc",
                  "address": "192.168.1.50:502",
                  "registers": [
                    { "name": "temperature", "address": 0, "topic": "factory/line1/temperature" },
                    { "name": "pressure", "address": 2, "topic": "factory/line1/pressure" }
                  ]
                }
              ]
            }
            """;

        var config = AppConfig.FromJson(json);

        Assert.Single(config.Modbus);
        Assert.Equal(2, config.Modbus[0].Registers.Count);
        Assert.Equal("temperature", config.Modbus[0].Registers[0].Name);
        Assert.Equal("pressure", config.Modbus[0].Registers[1].Name);
    }
}
