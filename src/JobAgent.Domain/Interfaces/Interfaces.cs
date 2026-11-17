using JobAgent.Domain.Entities;
using JobAgent.Domain.Models;

namespace JobAgent.Domain.Interfaces;

public interface IJobRepository
{
    Task InitAsync(CancellationToken ct = default);
    Task<bool> ExistsAsync(string id, string profileId, CancellationToken ct = default);
    Task SaveAsync(JobPosting job, JobScore score, string profileId, CancellationToken ct = default);
    Task<Job?> GetByIdAsync(string id, string profileId, CancellationToken ct = default);
    Task<PagedResult<Job>> GetPagedAsync(JobFilter filter, CancellationToken ct = default);
    Task<bool> UpdateStatusAsync(string id, string profileId, string status, string? notes = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, string profileId, CancellationToken ct = default);
    Task<JobStatistics> GetStatisticsAsync(string profileId, CancellationToken ct = default);
}

public interface ICandidateProfileRepository
{
    Task<IReadOnlyList<CandidateProfile>> GetAllAsync(CancellationToken ct = default);
    Task<CandidateProfile?> GetActiveAsync(CancellationToken ct = default);
    Task<CandidateProfile> EnsureDefaultAsync(CandidateProfile profile, CancellationToken ct = default);
    Task<CandidateProfile> SaveActiveAsync(CandidateProfile profile, CancellationToken ct = default);
    Task<CandidateProfile> CreateAndActivateAsync(CandidateProfile profile, CancellationToken ct = default);
    Task<bool> ActivateAsync(string id, CancellationToken ct = default);
}

public interface IJobScorer
{
    Task<JobScore?> ScoreAsync(JobPosting job, string candidateProfile, CancellationToken ct = default);
    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default);
}

public interface IJobSource
{
    string Name { get; }
    Task<IReadOnlyList<JobPosting>> FetchAsync(JobSearchContext context, CancellationToken ct = default);
}

public interface ITelegramNotifier
{
    Task SendAsync(JobPosting job, JobScore score, CancellationToken ct = default);
    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default);
}

public interface IProfileService
{
    Task<string> GetProfileAsync(CancellationToken ct = default);
    Task SaveProfileAsync(string content, CancellationToken ct = default);
    Task<CandidateProfile> GetActiveProfileAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CandidateProfile>> GetProfilesAsync(CancellationToken ct = default);
    Task<CandidateProfile> SaveActiveProfileAsync(
        string content,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default);
    Task<CandidateProfile> CreateProfileAsync(
        string name,
        string content,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default);
    Task<CandidateProfile?> ActivateProfileAsync(string id, CancellationToken ct = default);
    Task<CandidateProfile> UpdateActiveKeywordsAsync(
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default);
}

public interface IConfigurationService
{
    Task<AppConfigDto> GetConfigurationAsync(CancellationToken ct = default);
    Task SaveConfigurationAsync(AppConfigDto config, CancellationToken ct = default);
    Task<(bool Success, string Message)> TestEmailConnectionAsync(CancellationToken ct = default);
}

public interface IJobOrchestrator
{
    Task<RunResult> RunOnceAsync(CancellationToken ct = default);
    RunStatus GetCurrentStatus();
}
