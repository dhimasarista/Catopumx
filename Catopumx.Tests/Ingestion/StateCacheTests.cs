using System.Text.Json;
using Catopumx.Ingestion;
using Xunit;

namespace Catopumx.Tests.Ingestion;

public class StateCacheTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void FirstUpdateForATopicIsAlwaysChanged()
    {
        var cache = new StateCache();
        var outcome = cache.Update("sensors/a", "{\"v\":1}", Json("{\"v\":1}"));
        Assert.Equal(UpdateOutcome.Changed, outcome);
    }

    [Fact]
    public void IdenticalPayloadIsReportedUnchanged()
    {
        var cache = new StateCache();
        cache.Update("sensors/a", "{\"v\":1}", Json("{\"v\":1}"));
        var outcome = cache.Update("sensors/a", "{\"v\":1}", Json("{\"v\":1}"));
        Assert.Equal(UpdateOutcome.Unchanged, outcome);
    }

    [Fact]
    public void DifferentPayloadIsReportedChangedAndReplacesCachedValue()
    {
        var cache = new StateCache();
        cache.Update("sensors/a", "{\"v\":1}", Json("{\"v\":1}"));
        var outcome = cache.Update("sensors/a", "{\"v\":2}", Json("{\"v\":2}"));

        Assert.Equal(UpdateOutcome.Changed, outcome);
        Assert.Equal("{\"v\":2}", cache.Get("sensors/a")!.Payload.GetRawText());
    }

    [Fact]
    public void UnknownTopicReturnsNull()
    {
        var cache = new StateCache();
        Assert.Null(cache.Get("missing/topic"));
    }
}
