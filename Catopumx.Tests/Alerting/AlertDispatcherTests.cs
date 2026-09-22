using Catopumx.Alerting;
using Xunit;

namespace Catopumx.Tests.Alerting;

public class AlertDispatcherTests
{
    [Fact]
    public void RedactsUserinfoFromUrl() =>
        Assert.Equal(
            "https://***@example.com/hook",
            AlertDispatcher.RedactCredentials("https://user:pass@example.com/hook"));

    [Fact]
    public void LeavesUrlWithoutUserinfoUnchanged() =>
        Assert.Equal(
            "https://example.com/hook",
            AlertDispatcher.RedactCredentials("https://example.com/hook"));
}
