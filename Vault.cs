using System.Data.Common;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Catopumx;

/// <summary>
/// The database backend a connection string points at.
///
/// Unlike a single ORM abstraction, placeholder syntax ($1 vs ?) and UPSERT
/// syntax (ON CONFLICT vs ON DUPLICATE KEY UPDATE) differ per backend, so
/// every statement here is generated per-backend rather than written once
/// and assumed to be portable — mirroring the original sqlx::Any port.
/// </summary>
public enum Backend
{
    Postgres,
    MySql,
    Sqlite,
}

public static class BackendExtensions
{
    /// <summary>Detects the backend from a DATABASE_URL scheme.</summary>
    public static Backend? Detect(string databaseUrl)
    {
        if (databaseUrl.StartsWith("postgres:", StringComparison.Ordinal) ||
            databaseUrl.StartsWith("postgresql:", StringComparison.Ordinal))
        {
            return Backend.Postgres;
        }

        if (databaseUrl.StartsWith("mysql:", StringComparison.Ordinal) ||
            databaseUrl.StartsWith("mariadb:", StringComparison.Ordinal))
        {
            return Backend.MySql;
        }

        if (databaseUrl.StartsWith("sqlite:", StringComparison.Ordinal))
        {
            return Backend.Sqlite;
        }

        return null;
    }

    internal static string CreateTableSql(this Backend backend) => backend switch
    {
        Backend.Postgres or Backend.Sqlite =>
            """
            CREATE TABLE IF NOT EXISTS telemetry_latest (
                topic TEXT PRIMARY KEY,
                payload TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """,
        Backend.MySql =>
            """
            CREATE TABLE IF NOT EXISTS telemetry_latest (
                topic VARCHAR(255) PRIMARY KEY,
                payload TEXT NOT NULL,
                updated_at VARCHAR(64) NOT NULL
            )
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    internal static string UpsertSql(this Backend backend) => backend switch
    {
        Backend.Postgres =>
            """
            INSERT INTO telemetry_latest (topic, payload, updated_at) VALUES ($1, $2, $3)
            ON CONFLICT (topic) DO UPDATE SET payload = EXCLUDED.payload, updated_at = EXCLUDED.updated_at
            """,
        Backend.Sqlite =>
            """
            INSERT INTO telemetry_latest (topic, payload, updated_at) VALUES (@p0, @p1, @p2)
            ON CONFLICT(topic) DO UPDATE SET payload = excluded.payload, updated_at = excluded.updated_at
            """,
        Backend.MySql =>
            """
            INSERT INTO telemetry_latest (topic, payload, updated_at) VALUES (@p0, @p1, @p2)
            ON DUPLICATE KEY UPDATE payload = VALUES(payload), updated_at = VALUES(updated_at)
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    public static DbConnection CreateConnection(this Backend backend, string connectionString) => backend switch
    {
        Backend.Postgres => new NpgsqlConnection(connectionString),
        Backend.MySql => new MySqlConnection(connectionString),
        Backend.Sqlite => new SqliteConnection(connectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    /// <summary>Creates the telemetry_latest table if it doesn't already exist.</summary>
    public static async Task EnsureSchemaAsync(this Backend backend, DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = backend.CreateTableSql();
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Idempotently persists the latest payload for <paramref name="topic"/>.
    /// Values and parameters are always bound, never interpolated into the
    /// SQL text.
    /// </summary>
    public static async Task UpsertLatestAsync(this Backend backend, DbConnection connection, string topic, string payload)
    {
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");

        await using var command = connection.CreateCommand();
        command.CommandText = backend.UpsertSql();

        AddParameter(command, backend, 0, "$1", topic);
        AddParameter(command, backend, 1, "$2", payload);
        AddParameter(command, backend, 2, "$3", updatedAt);

        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, Backend backend, int index, string postgresName, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = backend == Backend.Postgres ? postgresName : $"@p{index}";
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
