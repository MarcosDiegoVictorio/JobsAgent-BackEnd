using System.Data.Common;
using System.Globalization;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Enums;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;

namespace JobAgent.Repository;

public sealed class JobRepository(
    DatabaseConnectionFactory connections,
    DatabaseInitializer initializer) : IJobRepository
{
    public Task InitAsync(CancellationToken ct = default) => initializer.InitializeAsync(ct);

    public async Task<bool> ExistsAsync(string id, string userId, string profileId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM jobs
            WHERE id = @id AND user_id = @userId AND profile_id = @profileId
            LIMIT 1
            """;
        Add(command, ("@id", id), ("@userId", userId), ("@profileId", profileId));
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<bool> SaveAsync(
        JobPosting job,
        JobScore score,
        string userId,
        string profileId,
        CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (connections.IsPostgreSql)
        {
            await using var lockUser = connection.CreateCommand();
            lockUser.Transaction = transaction;
            lockUser.CommandText = "SELECT id FROM users WHERE id = @userId FOR UPDATE";
            Add(lockUser, ("@userId", userId));
            if (await lockUser.ExecuteScalarAsync(ct) is null)
            {
                throw new InvalidOperationException("Usuário não encontrado.");
            }
        }

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM jobs WHERE user_id = @userId";
        Add(count, ("@userId", userId));
        var savedCount = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        if (savedCount >= 10)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs
                (id, user_id, profile_id, source, title, company, url, score, summary,
                 strengths, gaps, cv_tips, recruiter_message, status, notes, created_at)
            VALUES
                (@id, @userId, @profileId, @source, @title, @company, @url, @score, @summary,
                 @strengths, @gaps, @cv, @msg, 'novo', NULL, @created)
            ON CONFLICT (id) DO NOTHING
            """;
        Add(command,
            ("@id", job.Id),
            ("@userId", userId),
            ("@profileId", profileId),
            ("@source", job.Source),
            ("@title", job.Title),
            ("@company", job.Company),
            ("@url", job.Url),
            ("@score", score.Score),
            ("@summary", score.Summary),
            ("@strengths", string.Join(" | ", score.Strengths)),
            ("@gaps", string.Join(" | ", score.Gaps)),
            ("@cv", score.CvTips),
            ("@msg", score.RecruiterMessage),
            ("@created", DateTimeOffset.UtcNow.ToString("O")));
        var inserted = await command.ExecuteNonQueryAsync(ct) > 0;
        await transaction.CommitAsync(ct);
        return inserted;
    }

    public async Task<Job?> GetByIdAsync(string id, string userId, string profileId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM jobs
            WHERE id = @id AND user_id = @userId AND profile_id = @profileId
            LIMIT 1
            """;
        Add(command, ("@id", id), ("@userId", userId), ("@profileId", profileId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapJob(reader) : null;
    }

    public async Task<PagedResult<Job>> GetPagedAsync(JobFilter filter, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        var conditions = new List<string> { "user_id = @userId", "profile_id = @profileId" };
        var parameters = new List<(string Name, object Value)>
        {
            ("@userId", filter.UserId),
            ("@profileId", filter.ProfileId)
        };

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            conditions.Add("status = @status");
            parameters.Add(("@status", filter.Status.Trim().ToLowerInvariant()));
        }

        if (filter.MinScore.HasValue)
        {
            conditions.Add("score >= @minScore");
            parameters.Add(("@minScore", filter.MinScore.Value));
        }

        if (filter.MaxScore.HasValue)
        {
            conditions.Add("score <= @maxScore");
            parameters.Add(("@maxScore", filter.MaxScore.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            conditions.Add("source LIKE @source");
            parameters.Add(("@source", $"%{filter.Source.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            conditions.Add("(title LIKE @search OR company LIKE @search OR summary LIKE @search)");
            parameters.Add(("@search", $"%{filter.Search.Trim()}%"));
        }

        var whereSql = $"WHERE {string.Join(" AND ", conditions)}";
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM jobs {whereSql}";
        Add(countCommand, parameters.ToArray());
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

        var orderBy = filter.OrderBy?.ToLowerInvariant() switch
        {
            "created_at" or "date" => "created_at",
            "title" => "title",
            "company" => "company",
            _ => "score"
        };
        var direction = filter.Descending ? "DESC" : "ASC";
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        await using var query = connection.CreateCommand();
        query.CommandText = $"""
            SELECT * FROM jobs {whereSql}
            ORDER BY {orderBy} {direction}, created_at DESC
            LIMIT @limit OFFSET @offset
            """;
        Add(query, parameters.ToArray());
        Add(query, ("@limit", pageSize), ("@offset", (page - 1) * pageSize));

        var jobs = new List<Job>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            jobs.Add(MapJob(reader));
        }

        return new PagedResult<Job> { Items = jobs, TotalCount = totalCount, Page = page, PageSize = pageSize };
    }

    public async Task<bool> UpdateStatusAsync(
        string id,
        string userId,
        string profileId,
        string status,
        string? notes = null,
        CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = notes is null
            ? "UPDATE jobs SET status = @status WHERE id = @id AND user_id = @userId AND profile_id = @profileId"
            : "UPDATE jobs SET status = @status, notes = @notes WHERE id = @id AND user_id = @userId AND profile_id = @profileId";
        Add(command, ("@id", id), ("@userId", userId), ("@profileId", profileId),
            ("@status", status.Trim().ToLowerInvariant()));
        if (notes is not null)
        {
            Add(command, ("@notes", notes));
        }

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteAsync(
        string id,
        string userId,
        string profileId,
        CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM jobs WHERE id = @id AND user_id = @userId AND profile_id = @profileId
            """;
        Add(command, ("@id", id), ("@userId", userId), ("@profileId", profileId));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<JobStatistics> GetStatisticsAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        var stats = new JobStatistics();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*), COALESCE(AVG(score), 0.0),
                       SUM(CASE WHEN score >= 7 THEN 1 ELSE 0 END)
                FROM jobs WHERE user_id = @userId
                """;
            Add(command, ("@userId", userId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                stats.TotalJobs = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                stats.AverageScore = reader.IsDBNull(1) ? 0 : Math.Round(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture), 1);
                stats.HighScoreJobs = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT status, COUNT(*) FROM jobs WHERE user_id = @userId GROUP BY status";
            Add(command, ("@userId", userId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var status = reader.GetString(0);
                var count = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                stats.StatusDistribution[status] = count;
                switch (status.ToLowerInvariant())
                {
                    case JobStatus.Novo: stats.NewJobs = count; break;
                    case JobStatus.Aplicado: stats.AppliedJobs = count; break;
                    case JobStatus.Entrevista: stats.InterviewJobs = count; break;
                    case JobStatus.Descartada: stats.DiscardedJobs = count; break;
                    case JobStatus.Salvo: stats.SavedJobs = count; break;
                }
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT score, COUNT(*) FROM jobs WHERE user_id = @userId
                GROUP BY score ORDER BY score ASC
                """;
            Add(command, ("@userId", userId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                stats.ScoreDistribution[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT source, COUNT(*) FROM jobs WHERE user_id = @userId
                GROUP BY source ORDER BY COUNT(*) DESC LIMIT 10
                """;
            Add(command, ("@userId", userId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                stats.SourceDistribution[reader.GetString(0)] =
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }

        return stats;
    }

    public async Task<int> GetCountAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM jobs WHERE user_id = @userId";
        Add(command, ("@userId", userId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static Job MapJob(DbDataReader reader)
    {
        var strengthsRaw = reader.GetString(reader.GetOrdinal("strengths"));
        var gapsRaw = reader.GetString(reader.GetOrdinal("gaps"));
        var notesOrdinal = reader.GetOrdinal("notes");
        var createdAt = reader.GetValue(reader.GetOrdinal("created_at")).ToString() ?? string.Empty;

        return new Job
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Source = reader.GetString(reader.GetOrdinal("source")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Company = reader.GetString(reader.GetOrdinal("company")),
            Url = reader.GetString(reader.GetOrdinal("url")),
            Score = Convert.ToInt32(reader.GetValue(reader.GetOrdinal("score")), CultureInfo.InvariantCulture),
            Summary = reader.GetString(reader.GetOrdinal("summary")),
            Strengths = string.IsNullOrWhiteSpace(strengthsRaw) ? [] : strengthsRaw.Split(" | ", StringSplitOptions.RemoveEmptyEntries).ToList(),
            Gaps = string.IsNullOrWhiteSpace(gapsRaw) ? [] : gapsRaw.Split(" | ", StringSplitOptions.RemoveEmptyEntries).ToList(),
            CvTips = reader.GetString(reader.GetOrdinal("cv_tips")),
            RecruiterMessage = reader.GetString(reader.GetOrdinal("recruiter_message")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            Notes = reader.IsDBNull(notesOrdinal) ? null : reader.GetString(notesOrdinal),
            CreatedAt = DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
                ? date
                : DateTimeOffset.UtcNow
        };
    }

    private static void Add(DbCommand command, params (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            DatabaseInitializer.AddParameter(command, name, value);
        }
    }
}
