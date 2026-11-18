using JobAgent.Domain.Entities;
using JobAgent.Domain.Models;

namespace JobAgent.Domain.Interfaces;

public interface IJobRepository
{
    Task InitAsync(CancellationToken ct = default);
    Task<bool> ExistsAsync(string id, string userId, string profileId, CancellationToken ct = default);
    Task<bool> SaveAsync(JobPosting job, JobScore score, string userId, string profileId, CancellationToken ct = default);
    Task<Job?> GetByIdAsync(string id, string userId, string profileId, CancellationToken ct = default);
    Task<PagedResult<Job>> GetPagedAsync(JobFilter filter, CancellationToken ct = default);
    Task<bool> UpdateStatusAsync(string id, string userId, string profileId, string status, string? notes = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, string userId, string profileId, CancellationToken ct = default);
    Task<JobStatistics> GetStatisticsAsync(string userId, CancellationToken ct = default);
    Task<int> GetCountAsync(string userId, CancellationToken ct = default);
}

public interface ICandidateProfileRepository
{
    Task<IReadOnlyList<CandidateProfile>> GetAllAsync(string userId, CancellationToken ct = default);
    Task<CandidateProfile?> GetActiveAsync(string userId, CancellationToken ct = default);
    Task<CandidateProfile> EnsureDefaultAsync(string userId, CandidateProfile profile, CancellationToken ct = default);
    Task<CandidateProfile> SaveActiveAsync(string userId, CandidateProfile profile, CancellationToken ct = default);
    Task<CandidateProfile> CreateAndActivateAsync(string userId, CandidateProfile profile, CancellationToken ct = default);
    Task<bool> ActivateAsync(string userId, string id, CancellationToken ct = default);
    Task MigrateLegacyProfilesAsync(string userId, CancellationToken ct = default);
}

public interface IUserRepository
{
    Task InitAsync(CancellationToken ct = default);
    Task<string> CreateNonceAsync(CancellationToken ct = default);
    Task<bool> ConsumeNonceAsync(string nonce, CancellationToken ct = default);
    Task<AppUser> SignInOrInviteAsync(
        GoogleIdentity identity,
        string? inviteCode,
        string adminEmail,
        CancellationToken ct = default);
    Task<AppUser?> GetByIdAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<AdminUserDto>> GetUsersAsync(CancellationToken ct = default);
    Task<InvitationResponseDto> CreateInvitationAsync(
        string email,
        string inviteCode,
        string tokenHash,
        string createdBy,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);
    Task<int?> GrantSearchCreditAsync(string userId, string adminId, CancellationToken ct = default);
    Task<SearchReservationResult> ReserveSearchAsync(string userId, CancellationToken ct = default);
    Task CompleteSearchAsync(string runId, RunResult result, CancellationToken ct = default);
    Task<SearchRunRecord?> GetLatestSearchAsync(string userId, CancellationToken ct = default);
}

public sealed class GoogleIdentity
{
    public string Subject { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
}

public interface IAuthService
{
    Task<string> CreateNonceAsync(CancellationToken ct = default);
    Task<AuthResponseDto> SignInAsync(GoogleSignInRequest request, CancellationToken ct = default);
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
    Task<string> GetProfileAsync(string userId, CancellationToken ct = default);
    Task SaveProfileAsync(string userId, string content, CancellationToken ct = default);
    Task<CandidateProfile> GetActiveProfileAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<CandidateProfile>> GetProfilesAsync(string userId, CancellationToken ct = default);
    Task<CandidateProfile> SaveActiveProfileAsync(
        string userId,
        string content,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default);
    Task<CandidateProfile> CreateProfileAsync(
        string userId,
        string name,
        string content,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default);
    Task<CandidateProfile?> ActivateProfileAsync(string userId, string id, CancellationToken ct = default);
    Task<CandidateProfile> UpdateActiveKeywordsAsync(
        string userId,
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
    Task<RunResult> RunOnceAsync(string userId, string runId, CancellationToken ct = default);
    RunStatus GetCurrentStatus(string userId);
}
