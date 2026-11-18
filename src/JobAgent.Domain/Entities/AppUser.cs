namespace JobAgent.Domain.Entities;

public sealed class AppUser
{
    public string Id { get; set; } = string.Empty;
    public string GoogleSubject { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? PictureUrl { get; set; }
    public string Role { get; set; } = "user";
    public int SearchCreditsRemaining { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDisabled { get; set; }
}

public sealed class SearchRunRecord
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public int CollectedCount { get; set; }
    public int EvaluatedCount { get; set; }
    public string? Error { get; set; }
}
