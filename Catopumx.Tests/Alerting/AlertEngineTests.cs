using System.Text.Json;
using Catopumx.Alerting;
using Catopumx.Configuration;
using Xunit;

namespace Catopumx.Tests.Alerting;

public class AlertEngineTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    private static AlertRule Rule(string name, string topic, double threshold) => new()
    {
        Name = name,
        Topic = topic,
        Field = "value",
        Operator = Operator.GreaterThan,
        Threshold = threshold,
    };

    [Fact]
    public void FiresOnceOnTransitionIntoThreshold()
    {
        var engine = new AlertEngine([Rule("overheat", "sensors/temp", 80.0)]);
        var below = Json("""{"value": 70.0}""");
        var above = Json("""{"value": 90.0}""");

        Assert.Empty(engine.Evaluate("sensors/temp", below));
        Assert.Single(engine.Evaluate("sensors/temp", above));
        // Still above threshold on the next reading: no repeat alert.
        Assert.Empty(engine.Evaluate("sensors/temp", above));
    }

    [Fact]
    public void FiresAgainAfterDroppingBackBelowThreshold()
    {
        var engine = new AlertEngine([Rule("overheat", "sensors/temp", 80.0)]);
        var above = Json("""{"value": 90.0}""");
        var below = Json("""{"value": 50.0}""");

        Assert.Single(engine.Evaluate("sensors/temp", above));
        Assert.Empty(engine.Evaluate("sensors/temp", below));
        Assert.Single(engine.Evaluate("sensors/temp", above));
    }

    [Fact]
    public void IgnoresTopicsThatDoNotMatchAnyRule()
    {
        var engine = new AlertEngine([Rule("overheat", "sensors/temp", 80.0)]);
        var result = engine.Evaluate("sensors/other", Json("""{"value": 999.0}"""));
        Assert.Empty(result);
    }

    [Fact]
    public void IgnoresPayloadsMissingTheConfiguredField()
    {
        var engine = new AlertEngine([Rule("overheat", "sensors/temp", 80.0)]);
        var result = engine.Evaluate("sensors/temp", Json("""{"other_field": 999.0}"""));
        Assert.Empty(result);
    }
}
