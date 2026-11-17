namespace JobAgent.Domain.Entities;

public sealed class CandidateProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProfileMarkdown { get; set; } = string.Empty;
    public List<string> IncludeTitleKeywords { get; set; } = [];
    public List<string> ExcludeTitleKeywords { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
