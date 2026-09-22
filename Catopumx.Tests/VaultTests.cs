using Catopumx;
using Xunit;

namespace Catopumx.Tests;

public class VaultTests
{
    [Theory]
    [InlineData("postgres://u:p@host/db", Backend.Postgres)]
    [InlineData("postgresql://u:p@host/db", Backend.Postgres)]
    [InlineData("mysql://u:p@host/db", Backend.MySql)]
    [InlineData("mariadb://u:p@host/db", Backend.MySql)]
    [InlineData("sqlite://catopumx.db", Backend.Sqlite)]
    public void DetectsBackendFromUrlScheme(string url, Backend expected) =>
        Assert.Equal(expected, BackendExtensions.Detect(url));

    [Fact]
    public void UnknownSchemeReturnsNull() =>
        Assert.Null(BackendExtensions.Detect("mongodb://host/db"));

    [Fact]
    public void PostgresAndSqliteUseDistinctPlaceholderAndConflictSyntax()
    {
        Assert.Contains('$', Backend.Postgres.UpsertSql());
        Assert.DoesNotContain('$', Backend.Sqlite.UpsertSql());
        Assert.Contains("ON DUPLICATE KEY UPDATE", Backend.MySql.UpsertSql());
        Assert.Contains("ON CONFLICT", Backend.Postgres.UpsertSql());
    }
}
