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
    public async Task<string> GetProfileAsync(CancellationToken ct = default)
    {
        var profile = await GetActiveProfileAsync(ct);
        return profile.ProfileMarkdown;
    }

    public async Task SaveProfileAsync(string content, CancellationToken ct = default)
    {
        var current = await GetActiveProfileAsync(ct);
        current.ProfileMarkdown = content;
        await repository.SaveActiveAsync(current, ct);
        log.LogInformation("Perfil do candidato {ProfileId} salvo.", current.Id);
    }

    public async Task<CandidateProfile> GetActiveProfileAsync(CancellationToken ct = default)
    {
        var active = await repository.GetActiveAsync(ct);
        if (active is not null)
        {
            return active;
        }

        var filePath = ProfileFilePath;
        var markdown = File.Exists(filePath)
            ? await File.ReadAllTextAsync(filePath, ct)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            log.LogWarning("Arquivo de perfil não encontrado ou vazio em: {Path}", filePath);
        }

        return await repository.EnsureDefaultAsync(new CandidateProfile
        {
            Id = "default",
            Name = "Perfil padrão",
            ProfileMarkdown = markdown,
            IncludeTitleKeywords = options.CurrentValue.IncludeTitleKeywords,
            ExcludeTitleKeywords = options.CurrentValue.ExcludeTitleKeywords,
            IsActive = true
        }, ct);
    }

    public async Task<IReadOnlyList<CandidateProfile>> GetProfilesAsync(CancellationToken ct = default)
    {
        await GetActiveProfileAsync(ct);
        return await repository.GetAllAsync(ct);
    }

    public async Task<CandidateProfile> SaveActiveProfileAsync(
        string content,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default)
    {
        var active = await GetActiveProfileAsync(ct);
        active.ProfileMarkdown = content;
        active.IncludeTitleKeywords = NormalizeKeywords(includeKeywords);
        active.ExcludeTitleKeywords = NormalizeKeywords(excludeKeywords);
        return await repository.SaveActiveAsync(active, ct);
    }

    public async Task<CandidateProfile> CreateProfileAsync(
        string name,
        string content,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await repository.CreateAndActivateAsync(new CandidateProfile
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

    public async Task<CandidateProfile?> ActivateProfileAsync(string id, CancellationToken ct = default)
    {
        if (!await repository.ActivateAsync(id, ct))
        {
            return null;
        }

        return await GetActiveProfileAsync(ct);
    }

    public async Task<CandidateProfile> UpdateActiveKeywordsAsync(
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords,
        CancellationToken ct = default)
    {
        var active = await GetActiveProfileAsync(ct);
        active.IncludeTitleKeywords = NormalizeKeywords(includeKeywords);
        active.ExcludeTitleKeywords = NormalizeKeywords(excludeKeywords);
        return await repository.SaveActiveAsync(active, ct);
    }

    private static List<string> NormalizeKeywords(IEnumerable<string> keywords) =>
        keywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string ProfileFilePath
    {
        get
        {
            var path = options.CurrentValue.CandidateProfilePath;
            if (Path.IsPathRooted(path)) return path;

            var currentPath = Path.Combine(Directory.GetCurrentDirectory(), path);
            if (File.Exists(currentPath)) return currentPath;

            return Path.Combine(AppContext.BaseDirectory, path);
        }
    }
}
