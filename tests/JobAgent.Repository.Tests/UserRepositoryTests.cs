using System.Security.Cryptography;
using System.Text;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using JobAgent.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

namespace JobAgent.Repository.Tests;

public sealed class UserRepositoryTests
{
    [Fact]
    public async Task LegacySqliteMigrationImportsProfilesAndAtMostTenJobsOnce()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"jobagent-legacy-tests-{Guid.NewGuid():N}.db");
        try
        {
            await CreateLegacyDatabaseAsync(databasePath);
            var (users, jobs, profiles, connections) = CreateRepositories(databasePath);
            await users.InitAsync();
            var admin = await users.SignInOrInviteAsync(
                Identity("admin@example.test", "google-admin"),
                inviteCode: null,
                adminEmail: "admin@example.test");

            await profiles.MigrateLegacyProfilesAsync(admin.Id);
            await profiles.MigrateLegacyProfilesAsync(admin.Id);

            var profile = Assert.IsType<CandidateProfile>(await profiles.GetActiveAsync(admin.Id));
            Assert.Equal("Perfil legado", profile.Name);
            Assert.Equal([".NET"], profile.IncludeTitleKeywords);
            Assert.Equal(10, await jobs.GetCountAsync(admin.Id));
            var page = await jobs.GetPagedAsync(new JobFilter
            {
                UserId = admin.Id,
                ProfileId = profile.Id,
                PageSize = 20
            });
            Assert.Equal(10, page.TotalCount);
            Assert.Equal(Enumerable.Range(2, 10).Select(score => score).OrderByDescending(score => score),
                page.Items.Select(job => job.Score));

            await using var connection = await connections.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM jobs WHERE user_id = 'legacy'";
            Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task InvitesCreditsAndSavedJobsAreIsolatedAndCapped()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"jobagent-tests-{Guid.NewGuid():N}.db");
        try
        {
            var (users, jobs, profiles, connections) = CreateRepositories(databasePath);
            await users.InitAsync();

            var admin = await users.SignInOrInviteAsync(
                Identity("admin@example.test", "google-admin"),
                inviteCode: null,
                adminEmail: "ADMIN@example.test");
            Assert.Equal("admin", admin.Role);

            const string inviteCode = "one-time-invite";
            await using (var connection = await connections.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM invitations";
                Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }

            await users.CreateInvitationAsync(
                "first@example.test",
                inviteCode,
                Hash(inviteCode),
                admin.Id,
                DateTimeOffset.UtcNow.AddDays(7));
            var firstUser = await users.SignInOrInviteAsync(
                Identity("first@example.test", "google-first"),
                inviteCode,
                adminEmail: "admin@example.test");
            Assert.Equal(1, firstUser.SearchCreditsRemaining);

            var nonce = await users.CreateNonceAsync();
            Assert.True(await users.ConsumeNonceAsync(nonce));
            Assert.False(await users.ConsumeNonceAsync(nonce));

            var reservation = await users.ReserveSearchAsync(firstUser.Id);
            Assert.True(reservation.Accepted);
            var duplicateReservation = await users.ReserveSearchAsync(firstUser.Id);
            Assert.False(duplicateReservation.Accepted);
            await users.CompleteSearchAsync(reservation.RunId!, new RunResult());
            Assert.False((await users.ReserveSearchAsync(firstUser.Id)).Accepted);
            Assert.Equal(1, await users.GrantSearchCreditAsync(firstUser.Id, admin.Id));
            var nextReservation = await users.ReserveSearchAsync(firstUser.Id);
            Assert.True(nextReservation.Accepted);
            await users.CompleteSearchAsync(nextReservation.RunId!, new RunResult());

            const string secondInvite = "second-one-time-invite";
            await users.CreateInvitationAsync(
                "second@example.test",
                secondInvite,
                Hash(secondInvite),
                admin.Id,
                DateTimeOffset.UtcNow.AddDays(7));
            var secondUser = await users.SignInOrInviteAsync(
                Identity("second@example.test", "google-second"),
                secondInvite,
                adminEmail: "admin@example.test");

            var firstProfile = await profiles.EnsureDefaultAsync(firstUser.Id, NewProfile());
            var secondProfile = await profiles.EnsureDefaultAsync(secondUser.Id, NewProfile());
            Assert.NotEqual(firstProfile.Id, secondProfile.Id);
            Assert.Single(await profiles.GetAllAsync(firstUser.Id));
            Assert.Single(await profiles.GetAllAsync(secondUser.Id));

            string? firstJobId = null;
            for (var index = 0; index < 11; index++)
            {
                var job = new JobPosting(
                    $"job-{index}",
                    "test",
                    $"Developer {index}",
                    "Example Co",
                    $"https://example.test/jobs/{index}",
                    ".NET",
                    DateTimeOffset.UtcNow);
                var saved = await jobs.SaveAsync(job, new JobScore { Score = 8 }, secondUser.Id, secondProfile.Id);
                if (index < 10)
                {
                    Assert.True(saved);
                    firstJobId ??= job.Id;
                }
                else
                {
                    Assert.False(saved);
                }
            }

            Assert.Equal(10, await jobs.GetCountAsync(secondUser.Id));
            Assert.Equal(0, await jobs.GetCountAsync(firstUser.Id));
            Assert.Null(await jobs.GetByIdAsync(firstJobId!, firstUser.Id, secondProfile.Id));
            Assert.False((await users.ReserveSearchAsync(secondUser.Id)).Accepted);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static GoogleIdentity Identity(string email, string subject) => new()
    {
        Email = email,
        Subject = subject,
        DisplayName = email
    };

    private static CandidateProfile NewProfile() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "Perfil",
        ProfileMarkdown = "Perfil de teste",
        IsActive = true
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static (
        IUserRepository Users,
        IJobRepository Jobs,
        ICandidateProfileRepository Profiles,
        DatabaseConnectionFactory Connections) CreateRepositories(string databasePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "SQLite",
                ["Database:SqlitePath"] = databasePath
            })
            .Build();
        var connections = new DatabaseConnectionFactory(
            configuration,
            Options.Create(new DatabaseOptions { SqlitePath = databasePath }),
            Options.Create(new AgentOptions { DatabasePath = databasePath }));
        var initializer = new DatabaseInitializer(connections);
        return (
            new UserRepository(connections, initializer),
            new JobRepository(connections, initializer),
            new CandidateProfileRepository(connections),
            connections);
    }

    private static async Task CreateLegacyDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE candidate_profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                profile_markdown TEXT NOT NULL,
                is_active INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE candidate_profile_keywords (
                profile_id TEXT NOT NULL,
                keyword TEXT NOT NULL,
                is_excluded INTEGER NOT NULL,
                sort_order INTEGER NOT NULL,
                PRIMARY KEY (profile_id, is_excluded, sort_order)
            );
            CREATE TABLE jobs (
                id TEXT PRIMARY KEY,
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
            INSERT INTO candidate_profiles
                (id, name, profile_markdown, is_active, created_at, updated_at)
            VALUES ('legacy-profile', 'Perfil legado', 'Perfil anterior', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            INSERT INTO candidate_profile_keywords (profile_id, keyword, is_excluded, sort_order)
            VALUES ('legacy-profile', '.NET', 0, 0);
            """;
        await command.ExecuteNonQueryAsync();

        for (var score = 0; score < 12; score++)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO jobs
                    (id, profile_id, source, title, company, url, score, summary,
                     strengths, gaps, cv_tips, recruiter_message, status, notes, created_at)
                VALUES
                    (@id, 'legacy-profile', 'legacy', 'Developer', 'Example',
                     @url, @score, '', '', '', '', '', 'novo', NULL, @created)
                """;
            insert.Parameters.AddWithValue("@id", $"legacy-job-{score}");
            insert.Parameters.AddWithValue("@url", $"https://example.test/{score}");
            insert.Parameters.AddWithValue("@score", score);
            insert.Parameters.AddWithValue("@created", $"2026-01-{score + 1:00}T00:00:00Z");
            await insert.ExecuteNonQueryAsync();
        }
    }
}
