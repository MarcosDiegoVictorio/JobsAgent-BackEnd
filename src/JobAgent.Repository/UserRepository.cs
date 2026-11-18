using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;

namespace JobAgent.Repository;

public sealed class UserRepository(
    DatabaseConnectionFactory connections,
    DatabaseInitializer initializer) : IUserRepository
{
    public Task InitAsync(CancellationToken ct = default) => initializer.InitializeAsync(ct);

    public async Task<string> CreateNonceAsync(CancellationToken ct = default)
    {
        var nonce = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO auth_nonces (nonce_hash, expires_at) VALUES (@hash, @expiresAt)";
        Add(command, ("@hash", Hash(nonce)), ("@expiresAt", expiresAt.ToString("O")));
        await command.ExecuteNonQueryAsync(ct);
        return nonce;
    }

    public async Task<bool> ConsumeNonceAsync(string nonce, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM auth_nonces
            WHERE nonce_hash = @hash AND expires_at > @now
            """;
        Add(command,
            ("@hash", Hash(nonce)),
            ("@now", DateTimeOffset.UtcNow.ToString("O")));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<AppUser> SignInOrInviteAsync(
        GoogleIdentity identity,
        string? inviteCode,
        string adminEmail,
        CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(identity.Email);
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        await using (var findUser = connection.CreateCommand())
        {
            findUser.Transaction = transaction;
            findUser.CommandText = """
                SELECT id, google_subject, email, display_name, picture_url, role,
                       search_credits_remaining, created_at, is_disabled
                FROM users WHERE google_subject = @subject
                """;
            Add(findUser, ("@subject", identity.Subject));
            await using var reader = await findUser.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var existing = MapUser(reader);
                await reader.DisposeAsync();
                if (existing.IsDisabled)
                {
                    throw new UnauthorizedAccessException("Esta conta está desativada.");
                }

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE users SET email = @email, email_normalized = @normalized,
                        display_name = @name, picture_url = @picture
                    WHERE id = @id
                    """;
                Add(update,
                    ("@email", identity.Email),
                    ("@normalized", normalizedEmail),
                    ("@name", identity.DisplayName),
                    ("@picture", (object?)identity.PictureUrl ?? DBNull.Value),
                    ("@id", existing.Id));
                try
                {
                    await update.ExecuteNonQueryAsync(ct);
                }
                catch (DbException ex) when (IsUniqueConstraintViolation(ex))
                {
                    throw new UnauthorizedAccessException("Este e-mail já está associado a outra conta.", ex);
                }

                await transaction.CommitAsync(ct);
                existing.Email = identity.Email;
                existing.DisplayName = identity.DisplayName;
                existing.PictureUrl = identity.PictureUrl;
                return existing;
            }
        }

        var isAdmin = !string.IsNullOrWhiteSpace(adminEmail)
            && normalizedEmail.Equals(NormalizeEmail(adminEmail), StringComparison.Ordinal);
        var role = isAdmin ? "admin" : "user";
        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString("N"),
            GoogleSubject = identity.Subject,
            Email = identity.Email,
            DisplayName = identity.DisplayName,
            PictureUrl = identity.PictureUrl,
            Role = role
        };

        if (!isAdmin)
        {
            if (string.IsNullOrWhiteSpace(inviteCode))
            {
                throw new UnauthorizedAccessException("O acesso à aplicação é somente por convite.");
            }

            var invitationId = await FindInvitationAsync(
                connection, transaction, normalizedEmail, Hash(inviteCode), ct);
            if (invitationId is null)
            {
                throw new UnauthorizedAccessException("O convite é inválido, expirou ou já foi utilizado.");
            }

            await using var consumeInvite = connection.CreateCommand();
            consumeInvite.Transaction = transaction;
            consumeInvite.CommandText = """
                UPDATE invitations SET used_at = @now, redeemed_by = @userId
                WHERE id = @id AND used_at IS NULL AND expires_at > @now
                """;
            Add(consumeInvite,
                ("@now", DateTimeOffset.UtcNow.ToString("O")),
                ("@userId", user.Id),
                ("@id", invitationId));
            if (await consumeInvite.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new UnauthorizedAccessException("O convite é inválido, expirou ou já foi utilizado.");
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO users
                    (id, google_subject, email, email_normalized, display_name, picture_url,
                     role, search_credits_remaining, created_at, is_disabled)
                VALUES
                    (@id, @subject, @email, @normalized, @name, @picture,
                     @role, 1, @created, 0)
                """;
            Add(insert,
                ("@id", user.Id),
                ("@subject", user.GoogleSubject),
                ("@email", user.Email),
                ("@normalized", normalizedEmail),
                ("@name", user.DisplayName),
                ("@picture", (object?)user.PictureUrl ?? DBNull.Value),
                ("@role", user.Role),
                ("@created", user.CreatedAt.ToString("O")));
            try
            {
                await insert.ExecuteNonQueryAsync(ct);
            }
            catch (DbException ex) when (IsUniqueConstraintViolation(ex))
            {
                throw new UnauthorizedAccessException("Já existe uma conta associada a este e-mail.", ex);
            }
        }

        await transaction.CommitAsync(ct);
        return user;
    }

    public async Task<AppUser?> GetByIdAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, google_subject, email, display_name, picture_url, role,
                   search_credits_remaining, created_at, is_disabled
            FROM users WHERE id = @id
            """;
        Add(command, ("@id", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapUser(reader) : null;
    }

    public async Task<IReadOnlyList<AdminUserDto>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.id, u.email, u.display_name, u.role, u.search_credits_remaining,
                   (SELECT COUNT(*) FROM jobs j WHERE j.user_id = u.id)
            FROM users u
            ORDER BY u.created_at
            """;
        var users = new List<AdminUserDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            users.Add(new AdminUserDto
            {
                Id = reader.GetString(0),
                Email = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Role = reader.GetString(3),
                SearchCreditsRemaining = Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                SavedJobsCount = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture)
            });
        }

        return users;
    }

    public async Task<InvitationResponseDto> CreateInvitationAsync(
        string email,
        string inviteCode,
        string tokenHash,
        string createdBy,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var invitation = new InvitationResponseDto
        {
            Email = normalizedEmail,
            InviteCode = inviteCode,
            ExpiresAt = expiresAt
        };
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO invitations (id, email_normalized, token_hash, created_by, created_at, expires_at)
            VALUES (@id, @email, @hash, @createdBy, @created, @expiresAt)
            """;
        Add(command,
            ("@id", Guid.NewGuid().ToString("N")),
            ("@email", normalizedEmail),
            ("@hash", tokenHash),
            ("@createdBy", createdBy),
            ("@created", DateTimeOffset.UtcNow.ToString("O")),
            ("@expiresAt", expiresAt.ToString("O")));
        await command.ExecuteNonQueryAsync(ct);
        return invitation;
    }

    public async Task<int?> GrantSearchCreditAsync(string userId, string adminId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (connections.IsPostgreSql)
        {
            await using var lockUser = connection.CreateCommand();
            lockUser.Transaction = transaction;
            lockUser.CommandText = "SELECT id FROM users WHERE id = @userId FOR UPDATE";
            Add(lockUser, ("@userId", userId));
            if (await lockUser.ExecuteScalarAsync(ct) is null)
            {
                return null;
            }
        }

        await using (var grant = connection.CreateCommand())
        {
            grant.Transaction = transaction;
            grant.CommandText = """
                INSERT INTO search_credit_grants (id, user_id, granted_by, granted_at)
                VALUES (@id, @userId, @adminId, @now)
                """;
            Add(grant,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@userId", userId),
                ("@adminId", adminId),
                ("@now", DateTimeOffset.UtcNow.ToString("O")));
            try
            {
                await grant.ExecuteNonQueryAsync(ct);
            }
            catch (DbException ex) when (IsForeignKeyViolation(ex))
            {
                return null;
            }
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE users SET search_credits_remaining = search_credits_remaining + 1
            WHERE id = @userId AND is_disabled = 0
            """;
        Add(update, ("@userId", userId));
        if (await update.ExecuteNonQueryAsync(ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        await using var get = connection.CreateCommand();
        get.Transaction = transaction;
        get.CommandText = "SELECT search_credits_remaining FROM users WHERE id = @userId";
        Add(get, ("@userId", userId));
        var credits = Convert.ToInt32(await get.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(ct);
        return credits;
    }

    public async Task<SearchReservationResult> ReserveSearchAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        if (connections.IsPostgreSql)
        {
            await using var lockUser = connection.CreateCommand();
            lockUser.Transaction = transaction;
            lockUser.CommandText = "SELECT id FROM users WHERE id = @userId AND is_disabled = 0 FOR UPDATE";
            Add(lockUser, ("@userId", userId));
            if (await lockUser.ExecuteScalarAsync(ct) is null)
            {
                return new SearchReservationResult { Error = "Usuário não encontrado ou desativado." };
            }
        }

        await using (var running = connection.CreateCommand())
        {
            running.Transaction = transaction;
            running.CommandText = "SELECT 1 FROM search_runs WHERE user_id = @userId AND status = 'running' LIMIT 1";
            Add(running, ("@userId", userId));
            if (await running.ExecuteScalarAsync(ct) is not null)
            {
                return new SearchReservationResult { Error = "Já existe uma busca em andamento." };
            }
        }

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM jobs WHERE user_id = @userId";
        Add(count, ("@userId", userId));
        var savedCount = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        if (savedCount >= 10)
        {
            return new SearchReservationResult
            {
                SavedJobsCount = savedCount,
                Error = "O limite de 10 vagas salvas foi atingido."
            };
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE users SET search_credits_remaining = search_credits_remaining - 1
            WHERE id = @userId AND is_disabled = 0 AND search_credits_remaining > 0
            """;
        Add(update, ("@userId", userId));
        if (await update.ExecuteNonQueryAsync(ct) != 1)
        {
            return new SearchReservationResult
            {
                SavedJobsCount = savedCount,
                Error = "Você já utilizou sua busca disponível. Solicite ao administrador um crédito adicional."
            };
        }

        var runId = Guid.NewGuid().ToString("N");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO search_runs (id, user_id, status, started_at)
                VALUES (@id, @userId, 'running', @started)
                """;
            Add(insert,
                ("@id", runId),
                ("@userId", userId),
                ("@started", DateTimeOffset.UtcNow.ToString("O")));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using var remaining = connection.CreateCommand();
        remaining.Transaction = transaction;
        remaining.CommandText = "SELECT search_credits_remaining FROM users WHERE id = @userId";
        Add(remaining, ("@userId", userId));
        var remainingCredits = Convert.ToInt32(await remaining.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(ct);
        return new SearchReservationResult
        {
            Accepted = true,
            RunId = runId,
            RemainingCredits = remainingCredits,
            SavedJobsCount = savedCount
        };
    }

    public async Task CompleteSearchAsync(string runId, RunResult result, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE search_runs
            SET status = 'completed', completed_at = @completed,
                collected_count = @collected, evaluated_count = @evaluated, error = @error
            WHERE id = @id AND status = 'running'
            """;
        var errors = result.Errors.Count == 0 ? null : string.Join(Environment.NewLine, result.Errors);
        Add(command,
            ("@completed", DateTimeOffset.UtcNow.ToString("O")),
            ("@collected", result.CollectedJobs),
            ("@evaluated", result.EvaluatedJobs),
            ("@error", (object?)errors ?? DBNull.Value),
            ("@id", runId));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SearchRunRecord?> GetLatestSearchAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, status, started_at, completed_at, collected_count, evaluated_count, error
            FROM search_runs WHERE user_id = @userId
            ORDER BY started_at DESC LIMIT 1
            """;
        Add(command, ("@userId", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new SearchRunRecord
        {
            Id = reader.GetString(0),
            UserId = reader.GetString(1),
            Status = reader.GetString(2),
            StartedAt = ParseDate(reader.GetValue(3).ToString()),
            CompletedAt = reader.IsDBNull(4) ? null : ParseDate(reader.GetValue(4).ToString()),
            CollectedCount = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
            EvaluatedCount = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
            Error = reader.IsDBNull(7) ? null : reader.GetString(7)
        };
    }

    private async Task<string?> FindInvitationAsync(
        DbConnection connection,
        DbTransaction transaction,
        string email,
        string tokenHash,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = connections.IsPostgreSql
            ? """
              SELECT id FROM invitations
              WHERE email_normalized = @email AND token_hash = @hash
                AND used_at IS NULL AND expires_at > @now
              FOR UPDATE
              """
            : """
              SELECT id FROM invitations
              WHERE email_normalized = @email AND token_hash = @hash
                AND used_at IS NULL AND expires_at > @now
              """;
        Add(command,
            ("@email", email),
            ("@hash", tokenHash),
            ("@now", DateTimeOffset.UtcNow.ToString("O")));
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static AppUser MapUser(DbDataReader reader) => new()
    {
        Id = reader.GetString(0),
        GoogleSubject = reader.GetString(1),
        Email = reader.GetString(2),
        DisplayName = reader.GetString(3),
        PictureUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
        Role = reader.GetString(5),
        SearchCreditsRemaining = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
        CreatedAt = ParseDate(reader.GetValue(7).ToString()),
        IsDisabled = Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) == 1
    };

    private static void Add(DbCommand command, params (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            DatabaseInitializer.AddParameter(command, name, value);
        }
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : DateTimeOffset.UtcNow;

    private static bool IsUniqueConstraintViolation(DbException exception) =>
        exception is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
        || exception is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private static bool IsForeignKeyViolation(DbException exception) =>
        exception is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
        || exception is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation };
}
