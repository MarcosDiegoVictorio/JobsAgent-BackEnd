using System.Data.Common;
using System.Globalization;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;

namespace JobAgent.Repository;

public sealed class CandidateProfileRepository(DatabaseConnectionFactory connections) : ICandidateProfileRepository
{
    public async Task<IReadOnlyList<CandidateProfile>> GetAllAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.name, p.profile_markdown, p.is_active, p.created_at, p.updated_at,
                   k.keyword, k.is_excluded
            FROM user_profiles p
            LEFT JOIN user_profile_keywords k ON k.profile_id = p.id
            WHERE p.user_id = @userId
            ORDER BY p.is_active DESC, p.created_at, k.is_excluded, k.sort_order
            """;
        Add(command, ("@userId", userId));

        var profiles = new Dictionary<string, CandidateProfile>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
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
                    IsActive = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) == 1,
                    CreatedAt = ParseDate(reader.GetValue(4).ToString()),
                    UpdatedAt = ParseDate(reader.GetValue(5).ToString())
                };
                profiles.Add(id, profile);
            }

            if (reader.IsDBNull(6))
            {
                continue;
            }

            if (Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) == 1)
            {
                profile.ExcludeTitleKeywords.Add(reader.GetString(6));
            }
            else
            {
                profile.IncludeTitleKeywords.Add(reader.GetString(6));
            }
        }

        return profiles.Values.ToList();
    }

    public async Task<CandidateProfile?> GetActiveAsync(string userId, CancellationToken ct = default) =>
        (await GetAllAsync(userId, ct)).FirstOrDefault(profile => profile.IsActive);

    public async Task<CandidateProfile> EnsureDefaultAsync(
        string userId,
        CandidateProfile profile,
        CancellationToken ct = default)
    {
        var active = await GetActiveAsync(userId, ct);
        if (active is not null)
        {
            return active;
        }

        profile.Id = Guid.NewGuid().ToString("N");
        profile.Name = "Perfil padrão";
        profile.IsActive = true;
        return await CreateAndActivateAsync(userId, profile, ct);
    }

    public async Task<CandidateProfile> SaveActiveAsync(
        string userId,
        CandidateProfile profile,
        CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE user_profiles
            SET profile_markdown = @markdown, updated_at = @updated
            WHERE id = @id AND user_id = @userId AND is_active = 1
            """;
        Add(update,
            ("@markdown", profile.ProfileMarkdown),
            ("@updated", DateTimeOffset.UtcNow.ToString("O")),
            ("@id", profile.Id),
            ("@userId", userId));
        if (await update.ExecuteNonQueryAsync(ct) == 0)
        {
            throw new InvalidOperationException("O perfil ativo não foi encontrado.");
        }

        await ReplaceKeywordsAsync(connection, transaction, profile, ct);
        await transaction.CommitAsync(ct);
        return (await GetActiveAsync(userId, ct))!;
    }

    public async Task<CandidateProfile> CreateAndActivateAsync(
        string userId,
        CandidateProfile profile,
        CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var deactivate = connection.CreateCommand();
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE user_profiles SET is_active = 0 WHERE user_id = @userId AND is_active = 1";
            Add(deactivate, ("@userId", userId));
            await deactivate.ExecuteNonQueryAsync(ct);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO user_profiles
                    (id, user_id, name, name_normalized, profile_markdown, is_active, created_at, updated_at)
                VALUES
                    (@id, @userId, @name, @normalized, @markdown, 1, @created, @updated)
                """;
            Add(insert,
                ("@id", profile.Id),
                ("@userId", userId),
                ("@name", profile.Name),
                ("@normalized", NormalizeName(profile.Name)),
                ("@markdown", profile.ProfileMarkdown),
                ("@created", profile.CreatedAt.ToString("O")),
                ("@updated", profile.UpdatedAt.ToString("O")));
            await insert.ExecuteNonQueryAsync(ct);
            await InsertKeywordsAsync(connection, transaction, profile, ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbException ex) when (IsUniqueConstraintViolation(ex))
        {
            await transaction.RollbackAsync(ct);
            throw new InvalidOperationException("Já existe um perfil com esse nome.", ex);
        }

        return (await GetActiveAsync(userId, ct))!;
    }

    public async Task<bool> ActivateAsync(string userId, string id, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE user_profiles SET is_active = 0 WHERE user_id = @userId AND is_active = 1";
            Add(deactivate, ("@userId", userId));
            await deactivate.ExecuteNonQueryAsync(ct);
        }

        await using var activate = connection.CreateCommand();
        activate.Transaction = transaction;
        activate.CommandText = """
            UPDATE user_profiles SET is_active = 1, updated_at = @updated
            WHERE id = @id AND user_id = @userId
            """;
        Add(activate, ("@updated", DateTimeOffset.UtcNow.ToString("O")), ("@id", id), ("@userId", userId));
        if (await activate.ExecuteNonQueryAsync(ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task MigrateLegacyProfilesAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        if (!await LegacyProfilesExistAsync(connection, ct))
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);
        var migrationId = $"legacy-profiles-{userId}";
        await using var migrationCheck = connection.CreateCommand();
        migrationCheck.Transaction = transaction;
        migrationCheck.CommandText = "SELECT 1 FROM data_migrations WHERE id = @id";
        Add(migrationCheck, ("@id", migrationId));
        if (await migrationCheck.ExecuteScalarAsync(ct) is not null)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        await using var hasProfiles = connection.CreateCommand();
        hasProfiles.Transaction = transaction;
        hasProfiles.CommandText = "SELECT 1 FROM user_profiles WHERE user_id = @userId LIMIT 1";
        Add(hasProfiles, ("@userId", userId));
        if (await hasProfiles.ExecuteScalarAsync(ct) is null)
        {
            await using var copyProfiles = connection.CreateCommand();
            copyProfiles.Transaction = transaction;
            copyProfiles.CommandText = """
                INSERT INTO user_profiles
                    (id, user_id, name, name_normalized, profile_markdown, is_active, created_at, updated_at)
                SELECT id, @userId, name, LOWER(TRIM(name)), profile_markdown, is_active, created_at, updated_at
                FROM candidate_profiles
                """;
            Add(copyProfiles, ("@userId", userId));
            await copyProfiles.ExecuteNonQueryAsync(ct);

            await using var copyKeywords = connection.CreateCommand();
            copyKeywords.Transaction = transaction;
            copyKeywords.CommandText = """
                INSERT INTO user_profile_keywords (profile_id, keyword, is_excluded, sort_order)
                SELECT k.profile_id, k.keyword, k.is_excluded, k.sort_order
                FROM candidate_profile_keywords k
                JOIN user_profiles p ON p.id = k.profile_id AND p.user_id = @userId
                """;
            Add(copyKeywords, ("@userId", userId));
            await copyKeywords.ExecuteNonQueryAsync(ct);
        }

        await using var migrateJobs = connection.CreateCommand();
        migrateJobs.Transaction = transaction;
        migrateJobs.CommandText = """
            UPDATE jobs SET user_id = @userId
            WHERE id IN (
                SELECT id FROM jobs
                WHERE user_id = 'legacy'
                ORDER BY score DESC, created_at DESC
                LIMIT 10
            )
            """;
        Add(migrateJobs, ("@userId", userId));
        await migrateJobs.ExecuteNonQueryAsync(ct);

        await using var markMigration = connection.CreateCommand();
        markMigration.Transaction = transaction;
        markMigration.CommandText = "INSERT INTO data_migrations (id, applied_at) VALUES (@id, @appliedAt)";
        Add(markMigration,
            ("@id", migrationId),
            ("@appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        await markMigration.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<bool> LegacyProfilesExistAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = connections.IsPostgreSql
            ? "SELECT to_regclass('public.candidate_profiles')"
            : "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'candidate_profiles'";
        return await command.ExecuteScalarAsync(ct) is not null and not DBNull;
    }

    private static async Task ReplaceKeywordsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CandidateProfile profile,
        CancellationToken ct)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM user_profile_keywords WHERE profile_id = @profileId";
        Add(delete, ("@profileId", profile.Id));
        await delete.ExecuteNonQueryAsync(ct);
        await InsertKeywordsAsync(connection, transaction, profile, ct);
    }

    private static async Task InsertKeywordsAsync(
        DbConnection connection,
        DbTransaction transaction,
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
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO user_profile_keywords (profile_id, keyword, is_excluded, sort_order)
                    VALUES (@profileId, @keyword, @excluded, @sortOrder)
                    """;
                Add(command,
                    ("@profileId", profile.Id),
                    ("@keyword", uniqueKeywords[index]),
                    ("@excluded", excluded ? 1 : 0),
                    ("@sortOrder", index));
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        await InsertListAsync(profile.IncludeTitleKeywords, excluded: false);
        await InsertListAsync(profile.ExcludeTitleKeywords, excluded: true);
    }

    private static void Add(DbCommand command, params (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            DatabaseInitializer.AddParameter(command, name, value);
        }
    }

    private static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    private static bool IsUniqueConstraintViolation(DbException exception) =>
        exception is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
        || exception is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : DateTimeOffset.UtcNow;
}
