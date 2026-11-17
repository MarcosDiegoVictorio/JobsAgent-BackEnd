using System.Globalization;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using JobAgent.Domain.Models;

namespace JobAgent.Repository;

public sealed class CandidateProfileRepository(IOptions<AgentOptions> options) : ICandidateProfileRepository
{
    private string ConnectionString
    {
        get
        {
            var dbPath = options.Value.DatabasePath;
            if (!Path.IsPathRooted(dbPath))
            {
                dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
            }

            return $"Data Source={dbPath}";
        }
    }

    public async Task<IReadOnlyList<CandidateProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, p.profile_markdown, p.is_active, p.created_at, p.updated_at,
                   k.keyword, k.is_excluded
            FROM candidate_profiles p
            LEFT JOIN candidate_profile_keywords k ON k.profile_id = p.id
            ORDER BY p.is_active DESC, p.created_at, k.is_excluded, k.sort_order
            """;

        var profiles = new Dictionary<string, CandidateProfile>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            if (!profiles.TryGetValue(id, out var profile))
            {
                profile = new CandidateProfile
                {
                    Id = id,
                    Name = reader.GetString(1),
                    ProfileMarkdown = reader.GetString(2),
                    IsActive = reader.GetInt32(3) == 1,
                    CreatedAt = ParseDate(reader.GetString(4)),
                    UpdatedAt = ParseDate(reader.GetString(5))
                };
                profiles.Add(id, profile);
            }

            if (reader.IsDBNull(6))
            {
                continue;
            }

            var keyword = reader.GetString(6);
            if (reader.GetInt32(7) == 1)
            {
                profile.ExcludeTitleKeywords.Add(keyword);
            }
            else
            {
                profile.IncludeTitleKeywords.Add(keyword);
            }
        }

        return profiles.Values.ToList();
    }

    public async Task<CandidateProfile?> GetActiveAsync(CancellationToken ct = default)
    {
        var profiles = await GetAllAsync(ct);
        return profiles.FirstOrDefault(profile => profile.IsActive);
    }

    public async Task<CandidateProfile> EnsureDefaultAsync(CandidateProfile profile, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        using var transaction = conn.BeginTransaction();

        var activeCmd = conn.CreateCommand();
        activeCmd.Transaction = transaction;
        activeCmd.CommandText = "SELECT 1 FROM candidate_profiles WHERE is_active = 1 LIMIT 1";
        if (await activeCmd.ExecuteScalarAsync(ct) is not null)
        {
            await transaction.CommitAsync(ct);
            return (await GetActiveAsync(ct))!;
        }

        var defaultCmd = conn.CreateCommand();
        defaultCmd.Transaction = transaction;
        defaultCmd.CommandText = "SELECT 1 FROM candidate_profiles WHERE id = 'default' LIMIT 1";
        var defaultExists = await defaultCmd.ExecuteScalarAsync(ct) is not null;
        if (!defaultExists)
        {
            var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT INTO candidate_profiles (id, name, profile_markdown, is_active, created_at, updated_at)
                VALUES ('default', $name, $markdown, 1, $created, $updated)
                """;
            AddProfileParameters(insertCmd, profile);
            await insertCmd.ExecuteNonQueryAsync(ct);
            await InsertKeywordsAsync(conn, transaction, profile, ct);
        }
        else
        {
            var activateCmd = conn.CreateCommand();
            activateCmd.Transaction = transaction;
            activateCmd.CommandText = "UPDATE candidate_profiles SET is_active = 1 WHERE id = 'default'";
            await activateCmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return (await GetActiveAsync(ct))!;
    }

    public async Task<CandidateProfile> SaveActiveAsync(CandidateProfile profile, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        using var transaction = conn.BeginTransaction();

        var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = transaction;
        updateCmd.CommandText = """
            UPDATE candidate_profiles
            SET profile_markdown = $markdown, updated_at = $updated
            WHERE id = $id AND is_active = 1
            """;
        updateCmd.Parameters.AddWithValue("$markdown", profile.ProfileMarkdown);
        updateCmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        updateCmd.Parameters.AddWithValue("$id", profile.Id);
        if (await updateCmd.ExecuteNonQueryAsync(ct) == 0)
        {
            throw new InvalidOperationException("O perfil ativo não foi encontrado.");
        }

        await ReplaceKeywordsAsync(conn, transaction, profile, ct);
        await transaction.CommitAsync(ct);
        return (await GetActiveAsync(ct))!;
    }

    public async Task<CandidateProfile> CreateAndActivateAsync(CandidateProfile profile, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        using var transaction = conn.BeginTransaction();

        try
        {
            var deactivateCmd = conn.CreateCommand();
            deactivateCmd.Transaction = transaction;
            deactivateCmd.CommandText = "UPDATE candidate_profiles SET is_active = 0 WHERE is_active = 1";
            await deactivateCmd.ExecuteNonQueryAsync(ct);

            var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT INTO candidate_profiles (id, name, profile_markdown, is_active, created_at, updated_at)
                VALUES ($id, $name, $markdown, 1, $created, $updated)
                """;
            AddProfileParameters(insertCmd, profile);
            insertCmd.Parameters.AddWithValue("$id", profile.Id);
            await insertCmd.ExecuteNonQueryAsync(ct);
            await InsertKeywordsAsync(conn, transaction, profile, ct);
            await transaction.CommitAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(ct);
            throw new InvalidOperationException("Já existe um perfil com esse nome.", ex);
        }

        return (await GetActiveAsync(ct))!;
    }

    public async Task<bool> ActivateAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        using var transaction = conn.BeginTransaction();

        var deactivateCmd = conn.CreateCommand();
        deactivateCmd.Transaction = transaction;
        deactivateCmd.CommandText = "UPDATE candidate_profiles SET is_active = 0 WHERE is_active = 1";
        await deactivateCmd.ExecuteNonQueryAsync(ct);

        var activateCmd = conn.CreateCommand();
        activateCmd.Transaction = transaction;
        activateCmd.CommandText = "UPDATE candidate_profiles SET is_active = 1 WHERE id = $id";
        activateCmd.Parameters.AddWithValue("$id", id);
        if (await activateCmd.ExecuteNonQueryAsync(ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON";
        await pragma.ExecuteNonQueryAsync(ct);
        return conn;
    }

    private static void AddProfileParameters(SqliteCommand cmd, CandidateProfile profile)
    {
        cmd.Parameters.AddWithValue("$name", profile.Name);
        cmd.Parameters.AddWithValue("$markdown", profile.ProfileMarkdown);
        cmd.Parameters.AddWithValue("$created", profile.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", profile.UpdatedAt.ToString("O"));
    }

    private static async Task ReplaceKeywordsAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        CandidateProfile profile,
        CancellationToken ct)
    {
        var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = transaction;
        deleteCmd.CommandText = "DELETE FROM candidate_profile_keywords WHERE profile_id = $id";
        deleteCmd.Parameters.AddWithValue("$id", profile.Id);
        await deleteCmd.ExecuteNonQueryAsync(ct);
        await InsertKeywordsAsync(conn, transaction, profile, ct);
    }

    private static async Task InsertKeywordsAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        CandidateProfile profile,
        CancellationToken ct)
    {
        async Task InsertListAsync(IReadOnlyCollection<string> keywords, bool excluded)
        {
            var uniqueKeywords = keywords
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Select(keyword => keyword.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var index = 0; index < uniqueKeywords.Count; index++)
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO candidate_profile_keywords (profile_id, keyword, is_excluded, sort_order)
                    VALUES ($profileId, $keyword, $excluded, $sortOrder)
                    """;
                cmd.Parameters.AddWithValue("$profileId", profile.Id);
                cmd.Parameters.AddWithValue("$keyword", uniqueKeywords[index]);
                cmd.Parameters.AddWithValue("$excluded", excluded ? 1 : 0);
                cmd.Parameters.AddWithValue("$sortOrder", index);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await InsertListAsync(profile.IncludeTitleKeywords, excluded: false);
        await InsertListAsync(profile.ExcludeTitleKeywords, excluded: true);
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : DateTimeOffset.UtcNow;
}
