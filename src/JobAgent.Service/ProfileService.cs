using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobAgent.Service;

public sealed class ProfileService(
    ICandidateProfileRepository repository,
    IOptionsMonitor<AgentOptions> options,
    ILogger<ProfileService> log) : IProfileService
{
    public async Task<string> GetProfileAsync(string userId, CancellationToken ct = default)
    {
        var profile = await GetActiveProfileAsync(userId, ct);
        return profile.ProfileMarkdown;
    }

    public async Task SaveProfileAsync(string userId, string content, CancellationToken ct = default)
    {
        var current = await GetActiveProfileAsync(userId, ct);
        current.ProfileMarkdown = content;
        await repository.SaveActiveAsync(userId, current, ct);
        log.LogInformation("Perfil do candidato {ProfileId} salvo.", current.Id);
    }

    public async Task<CandidateProfile> GetActiveProfileAsync(string userId, CancellationToken ct = default)
    {
        var active = await repository.GetActiveAsync(userId, ct);
        if (active is not null)
        {
            return active;
        }

        log.LogInformation("Criando perfil inicial vazio para usuário {UserId}.", userId);
        return await repository.EnsureDefaultAsync(userId, new CandidateProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Perfil padrão",
            ProfileMarkdown = string.Empty,
            IncludeTitleKeywords = options.CurrentValue.IncludeTitleKeywords,
            ExcludeTitleKeywords = options.CurrentValue.ExcludeTitleKeywords,
            IsActive = true
        }, ct);
    }

    public async Task<IReadOnlyList<CandidateProfile>> GetProfilesAsync(string userId, CancellationToken ct = default)
    {
        await GetActiveProfileAsync(userId, ct);
        return await repository.GetAllAsync(userId, ct);
    }

    public async Task<CandidateProfile> SaveActiveProfileAsync(
        string userId,
        string content,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default)
    {
        var active = await GetActiveProfileAsync(userId, ct);
        active.ProfileMarkdown = content;
        active.IncludeTitleKeywords = NormalizeKeywords(includeKeywords);
        active.ExcludeTitleKeywords = NormalizeKeywords(excludeKeywords);
        return await repository.SaveActiveAsync(userId, active, ct);
    }

    public async Task<CandidateProfile> CreateProfileAsync(
        string userId,
        string name,
        string content,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await repository.CreateAndActivateAsync(userId, new CandidateProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            ProfileMarkdown = content,
            IncludeTitleKeywords = NormalizeKeywords(includeKeywords),
            ExcludeTitleKeywords = NormalizeKeywords(excludeKeywords),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        }, ct);
    }

    public async Task<CandidateProfile?> ActivateProfileAsync(string userId, string id, CancellationToken ct = default)
    {
        if (!await repository.ActivateAsync(userId, id, ct))
        {
            return null;
        }

        return await GetActiveProfileAsync(userId, ct);
    }

    public async Task<CandidateProfile> UpdateActiveKeywordsAsync(
        string userId,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default)
    {
        var active = await GetActiveProfileAsync(userId, ct);
        active.IncludeTitleKeywords = NormalizeKeywords(includeKeywords);
        active.ExcludeTitleKeywords = NormalizeKeywords(excludeKeywords);
        return await repository.SaveActiveAsync(userId, active, ct);
    }

    private static List<string> NormalizeKeywords(IEnumerable<string> keywords) =>
        keywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

}
