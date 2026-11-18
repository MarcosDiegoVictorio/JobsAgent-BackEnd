using System.Data.Common;
using JobAgent.Domain.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace JobAgent.Repository;

public sealed class DatabaseConnectionFactory(
    IConfiguration configuration,
    IOptions<DatabaseOptions> options,
    IOptions<AgentOptions> agentOptions)
{
    private readonly string? configuredConnectionString = configuration.GetConnectionString("JobAgent");
    private readonly string provider = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("JobAgent"))
        ? "PostgreSQL"
        : options.Value.Provider;

    public DbConnection CreateConnection()
    {
        if (provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuredConnectionString ?? options.Value.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Configure ConnectionStrings:JobAgent com a conexão PostgreSQL.");
            }

            return new NpgsqlConnection(connectionString);
        }

        var path = configuration.GetSection("Database:SqlitePath").Exists()
            ? options.Value.SqlitePath
            : agentOptions.Value.DatabasePath;
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, path);
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new SqliteConnection($"Data Source={path}");
    }

    public bool IsPostgreSql => provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase);

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = CreateConnection();
        await connection.OpenAsync(ct);

        if (connection is SqliteConnection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON";
            await command.ExecuteNonQueryAsync(ct);
        }

        return connection;
    }
}

public sealed class DatabaseInitializer(DatabaseConnectionFactory connections)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY,
                google_subject TEXT NOT NULL UNIQUE,
                email TEXT NOT NULL,
                email_normalized TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                picture_url TEXT,
                role TEXT NOT NULL,
                search_credits_remaining INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                is_disabled INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS invitations (
                id TEXT PRIMARY KEY,
                email_normalized TEXT NOT NULL,
                token_hash TEXT NOT NULL UNIQUE,
                created_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                used_at TEXT,
                redeemed_by TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_invitations_email ON invitations(email_normalized);
            CREATE TABLE IF NOT EXISTS auth_nonces (
                nonce_hash TEXT PRIMARY KEY,
                expires_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS user_profiles (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                name_normalized TEXT NOT NULL,
                profile_markdown TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(user_id, name_normalized)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_user_profiles_active
                ON user_profiles(user_id) WHERE is_active = 1;
            CREATE TABLE IF NOT EXISTS user_profile_keywords (
                profile_id TEXT NOT NULL REFERENCES user_profiles(id) ON DELETE CASCADE,
                keyword TEXT NOT NULL,
                is_excluded INTEGER NOT NULL,
                sort_order INTEGER NOT NULL,
                PRIMARY KEY (profile_id, is_excluded, sort_order)
            );
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL DEFAULT 'legacy',
                profile_id TEXT NOT NULL DEFAULT 'default',
                source TEXT NOT NULL,
                title TEXT NOT NULL,
                company TEXT NOT NULL,
                url TEXT NOT NULL,
                score INTEGER NOT NULL,
                summary TEXT NOT NULL,
                strengths TEXT NOT NULL,
                gaps TEXT NOT NULL,
                cv_tips TEXT NOT NULL,
                recruiter_message TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'novo',
                notes TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS search_runs (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL REFERENCES users(id),
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT,
                collected_count INTEGER NOT NULL DEFAULT 0,
                evaluated_count INTEGER NOT NULL DEFAULT 0,
                error TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_search_runs_one_running_per_user
                ON search_runs(user_id) WHERE status = 'running';
            CREATE TABLE IF NOT EXISTS search_credit_grants (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL REFERENCES users(id),
                granted_by TEXT NOT NULL REFERENCES users(id),
                granted_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_search_credit_grants_user ON search_credit_grants(user_id);
            CREATE TABLE IF NOT EXISTS data_migrations (
                id TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);

        await EnsureLegacyJobColumnsAsync(connection, ct);
        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_jobs_user_created ON jobs(user_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_jobs_user_profile ON jobs(user_id, profile_id);
                CREATE INDEX IF NOT EXISTS idx_jobs_user_score ON jobs(user_id, score);
                """;
            await indexes.ExecuteNonQueryAsync(ct);
        }

        await using var cleanup = connection.CreateCommand();
        cleanup.CommandText = "DELETE FROM auth_nonces WHERE expires_at < @now";
        AddParameter(cleanup, "@now", DateTimeOffset.UtcNow.ToString("O"));
        await cleanup.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureLegacyJobColumnsAsync(DbConnection connection, CancellationToken ct)
    {
        if (connections.IsPostgreSql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE jobs ADD COLUMN IF NOT EXISTS user_id TEXT NOT NULL DEFAULT 'legacy';
                ALTER TABLE jobs ADD COLUMN IF NOT EXISTS profile_id TEXT NOT NULL DEFAULT 'default';
                ALTER TABLE jobs ADD COLUMN IF NOT EXISTS notes TEXT;
                """;
            await command.ExecuteNonQueryAsync(ct);
            return;
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(jobs)";
            await using var reader = await pragma.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var (name, definition) in new[]
                 {
                     ("user_id", "TEXT NOT NULL DEFAULT 'legacy'"),
                     ("profile_id", "TEXT NOT NULL DEFAULT 'default'"),
                     ("notes", "TEXT")
                 })
        {
            if (columns.Contains(name))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE jobs ADD COLUMN {name} {definition}";
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    internal static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
