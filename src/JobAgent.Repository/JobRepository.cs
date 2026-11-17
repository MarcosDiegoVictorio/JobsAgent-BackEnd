using System.Globalization;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Enums;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace JobAgent.Repository;

public sealed class JobRepository(IOptions<AgentOptions> options) : IJobRepository
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

    public async Task InitAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
                id                TEXT PRIMARY KEY,
                profile_id        TEXT NOT NULL DEFAULT 'default',
                source            TEXT NOT NULL,
                title             TEXT NOT NULL,
                company           TEXT NOT NULL,
                url               TEXT NOT NULL,
                score             INTEGER NOT NULL,
                summary           TEXT NOT NULL,
                strengths         TEXT NOT NULL,
                gaps              TEXT NOT NULL,
                cv_tips           TEXT NOT NULL,
                recruiter_message TEXT NOT NULL,
                status            TEXT NOT NULL DEFAULT 'novo',
                notes             TEXT,
                created_at        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_jobs_score ON jobs(score);
            CREATE INDEX IF NOT EXISTS idx_jobs_status ON jobs(status);
            CREATE INDEX IF NOT EXISTS idx_jobs_created_at ON jobs(created_at);
            CREATE TABLE IF NOT EXISTS candidate_profiles (
                id              TEXT PRIMARY KEY,
                name            TEXT NOT NULL COLLATE NOCASE UNIQUE,
                profile_markdown TEXT NOT NULL,
                is_active       INTEGER NOT NULL DEFAULT 0,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_candidate_profiles_active
                ON candidate_profiles(is_active) WHERE is_active = 1;
            CREATE TABLE IF NOT EXISTS candidate_profile_keywords (
                profile_id  TEXT NOT NULL REFERENCES candidate_profiles(id) ON DELETE CASCADE,
                keyword     TEXT NOT NULL COLLATE NOCASE,
                is_excluded INTEGER NOT NULL,
                sort_order  INTEGER NOT NULL,
                PRIMARY KEY (profile_id, is_excluded, sort_order)
            );
            CREATE INDEX IF NOT EXISTS idx_candidate_profile_keywords_profile
                ON candidate_profile_keywords(profile_id);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Auto migration for existing databases lacking 'notes' column
        try
        {
            var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE jobs ADD COLUMN notes TEXT;";
            await alterCmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // duplicate column name
        {
            // Column already exists
        }

        var columnsCmd = conn.CreateCommand();
        columnsCmd.CommandText = "PRAGMA table_info(jobs);";
        var hasProfileId = false;
        await using (var reader = await columnsCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetString(1).Equals("profile_id", StringComparison.OrdinalIgnoreCase))
                {
                    hasProfileId = true;
                    break;
                }
            }
        }

        if (!hasProfileId)
        {
            var migrateCmd = conn.CreateCommand();
            migrateCmd.CommandText = "ALTER TABLE jobs ADD COLUMN profile_id TEXT NOT NULL DEFAULT 'default';";
            await migrateCmd.ExecuteNonQueryAsync(ct);
        }

        var profileIndexCmd = conn.CreateCommand();
        profileIndexCmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_jobs_profile_created ON jobs(profile_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_jobs_profile_score ON jobs(profile_id, score);
            """;
        await profileIndexCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ExistsAsync(string id, string profileId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM jobs WHERE id = $id AND profile_id = $profileId LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$profileId", profileId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task SaveAsync(JobPosting job, JobScore score, string profileId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO jobs
                (id, profile_id, source, title, company, url, score, summary, strengths, gaps, cv_tips, recruiter_message, status, notes, created_at)
            VALUES
                ($id, $profileId, $source, $title, $company, $url, $score, $summary, $strengths, $gaps, $cv, $msg, 'novo', NULL, $created);
            """;
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$source", job.Source);
        cmd.Parameters.AddWithValue("$title", job.Title);
        cmd.Parameters.AddWithValue("$company", job.Company);
        cmd.Parameters.AddWithValue("$url", job.Url);
        cmd.Parameters.AddWithValue("$score", score.Score);
        cmd.Parameters.AddWithValue("$summary", score.Summary);
        cmd.Parameters.AddWithValue("$strengths", string.Join(" | ", score.Strengths));
        cmd.Parameters.AddWithValue("$gaps", string.Join(" | ", score.Gaps));
        cmd.Parameters.AddWithValue("$cv", score.CvTips);
        cmd.Parameters.AddWithValue("$msg", score.RecruiterMessage);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Job?> GetByIdAsync(string id, string profileId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE id = $id AND profile_id = $profileId LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapJob(reader);
        }

        return null;
    }

    public async Task<PagedResult<Job>> GetPagedAsync(JobFilter filter, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var whereClauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        whereClauses.Add("profile_id = $profileId");
        parameters.Add(new SqliteParameter("$profileId", filter.ProfileId));

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            whereClauses.Add("status = $status");
            parameters.Add(new SqliteParameter("$status", filter.Status.Trim().ToLowerInvariant()));
        }

        if (filter.MinScore.HasValue)
        {
            whereClauses.Add("score >= $minScore");
            parameters.Add(new SqliteParameter("$minScore", filter.MinScore.Value));
        }

        if (filter.MaxScore.HasValue)
        {
            whereClauses.Add("score <= $maxScore");
            parameters.Add(new SqliteParameter("$maxScore", filter.MaxScore.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            whereClauses.Add("source LIKE $source");
            parameters.Add(new SqliteParameter("$source", $"%{filter.Source.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            whereClauses.Add("(title LIKE $search OR company LIKE $search OR summary LIKE $search)");
            parameters.Add(new SqliteParameter("$search", $"%{filter.Search.Trim()}%"));
        }

        var whereSql = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : "";

        // Count total
        var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM jobs {whereSql}";
        foreach (var p in parameters)
        {
            countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        }

        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // Query items
        var orderByCol = filter.OrderBy?.ToLowerInvariant() switch
        {
            "created_at" or "date" => "created_at",
            "title" => "title",
            "company" => "company",
            _ => "score"
        };
        var direction = filter.Descending ? "DESC" : "ASC";

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var offset = (page - 1) * pageSize;

        var queryCmd = conn.CreateCommand();
        queryCmd.CommandText = $"""
            SELECT * FROM jobs
            {whereSql}
            ORDER BY {orderByCol} {direction}, created_at DESC
            LIMIT $limit OFFSET $offset
            """;

        foreach (var p in parameters)
        {
            queryCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        }
        queryCmd.Parameters.AddWithValue("$limit", pageSize);
        queryCmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<Job>();
        await using var reader = await queryCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapJob(reader));
        }

        return new PagedResult<Job>
        {
            Items = list,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<bool> UpdateStatusAsync(string id, string profileId, string status, string? notes = null, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        if (notes is not null)
        {
            cmd.CommandText = "UPDATE jobs SET status = $status, notes = $notes WHERE id = $id AND profile_id = $profileId";
            cmd.Parameters.AddWithValue("$notes", notes);
        }
        else
        {
            cmd.CommandText = "UPDATE jobs SET status = $status WHERE id = $id AND profile_id = $profileId";
        }

        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$status", status.Trim().ToLowerInvariant());

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(string id, string profileId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE id = $id AND profile_id = $profileId";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$profileId", profileId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<JobStatistics> GetStatisticsAsync(string profileId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        var stats = new JobStatistics();

        // Status counts & averages
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 
                COUNT(*) as total,
                COALESCE(AVG(score), 0.0) as avg_score,
                SUM(CASE WHEN score >= 7 THEN 1 ELSE 0 END) as high_score
            FROM jobs
            WHERE profile_id = $profileId
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                stats.TotalJobs = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                stats.AverageScore = reader.IsDBNull(1) ? 0.0 : Math.Round(reader.GetDouble(1), 1);
                stats.HighScoreJobs = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            }
        }

        // Status breakdown
        var statusCmd = conn.CreateCommand();
        statusCmd.CommandText = "SELECT status, COUNT(*) FROM jobs WHERE profile_id = $profileId GROUP BY status";
        statusCmd.Parameters.AddWithValue("$profileId", profileId);
        await using (var reader = await statusCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var s = reader.GetString(0);
                var count = reader.GetInt32(1);
                stats.StatusDistribution[s] = count;

                switch (s.ToLowerInvariant())
                {
                    case JobStatus.Novo: stats.NewJobs = count; break;
                    case JobStatus.Aplicado: stats.AppliedJobs = count; break;
                    case JobStatus.Entrevista: stats.InterviewJobs = count; break;
                    case JobStatus.Descartada: stats.DiscardedJobs = count; break;
                    case JobStatus.Salvo: stats.SavedJobs = count; break;
                }
            }
        }

        // Score breakdown
        var scoreCmd = conn.CreateCommand();
        scoreCmd.CommandText = "SELECT score, COUNT(*) FROM jobs WHERE profile_id = $profileId GROUP BY score ORDER BY score ASC";
        scoreCmd.Parameters.AddWithValue("$profileId", profileId);
        await using (var reader = await scoreCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                stats.ScoreDistribution[reader.GetInt32(0)] = reader.GetInt32(1);
            }
        }

        // Source breakdown
        var srcCmd = conn.CreateCommand();
        srcCmd.CommandText = "SELECT source, COUNT(*) FROM jobs WHERE profile_id = $profileId GROUP BY source ORDER BY COUNT(*) DESC LIMIT 10";
        srcCmd.Parameters.AddWithValue("$profileId", profileId);
        await using (var reader = await srcCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                stats.SourceDistribution[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        return stats;
    }

    private static Job MapJob(SqliteDataReader reader)
    {
        var strengthsRaw = reader.GetString(reader.GetOrdinal("strengths"));
        var gapsRaw = reader.GetString(reader.GetOrdinal("gaps"));

        var notesOrdinal = -1;
        try { notesOrdinal = reader.GetOrdinal("notes"); } catch { }

        return new Job
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Source = reader.GetString(reader.GetOrdinal("source")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Company = reader.GetString(reader.GetOrdinal("company")),
            Url = reader.GetString(reader.GetOrdinal("url")),
            Score = reader.GetInt32(reader.GetOrdinal("score")),
            Summary = reader.GetString(reader.GetOrdinal("summary")),
            Strengths = string.IsNullOrWhiteSpace(strengthsRaw) ? [] : strengthsRaw.Split(" | ", StringSplitOptions.RemoveEmptyEntries).ToList(),
            Gaps = string.IsNullOrWhiteSpace(gapsRaw) ? [] : gapsRaw.Split(" | ", StringSplitOptions.RemoveEmptyEntries).ToList(),
            CvTips = reader.GetString(reader.GetOrdinal("cv_tips")),
            RecruiterMessage = reader.GetString(reader.GetOrdinal("recruiter_message")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            Notes = notesOrdinal >= 0 && !reader.IsDBNull(notesOrdinal) ? reader.GetString(notesOrdinal) : null,
            CreatedAt = DateTimeOffset.TryParse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : DateTimeOffset.UtcNow
        };
    }
}
