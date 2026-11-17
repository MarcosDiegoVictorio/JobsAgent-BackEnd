namespace JobAgent.Domain.Entities;

public sealed class Job
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> Strengths { get; set; } = [];
    public List<string> Gaps { get; set; } = [];
    public string CvTips { get; set; } = string.Empty;
    public string RecruiterMessage { get; set; } = string.Empty;
    public string Status { get; set; } = "novo";
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
