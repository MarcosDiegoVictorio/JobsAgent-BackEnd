namespace JobAgent.Domain.Models;

public sealed class AppConfigDto
{
    public AgentOptions Agent { get; set; } = new();
    public GeminiOptions Gemini { get; set; } = new();
    public AnthropicOptions Anthropic { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public RssOptions Rss { get; set; } = new();
    public EmailOptions Email { get; set; } = new();
}

public sealed class JobFilter
{
    public string ProfileId { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Status { get; set; }
    public int? MinScore { get; set; }
    public int? MaxScore { get; set; }
    public string? Source { get; set; }
    public string? Search { get; set; }
    public string? OrderBy { get; set; } = "score";
    public bool Descending { get; set; } = true;
}

public sealed class JobSearchContext
{
    public string ProfileId { get; init; } = string.Empty;
    public IReadOnlyList<string> Keywords { get; init; } = [];
}

public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}

public sealed class JobStatistics
{
    public int TotalJobs { get; set; }
    public int NewJobs { get; set; }
    public int AppliedJobs { get; set; }
    public int InterviewJobs { get; set; }
    public int DiscardedJobs { get; set; }
    public int SavedJobs { get; set; }
    public double AverageScore { get; set; }
    public int HighScoreJobs { get; set; }
    public Dictionary<string, int> StatusDistribution { get; set; } = [];
    public Dictionary<int, int> ScoreDistribution { get; set; } = [];
    public Dictionary<string, int> SourceDistribution { get; set; } = [];
}

public sealed class RunStatus
{
    public bool IsRunning { get; set; }
    public DateTimeOffset? LastRunTime { get; set; }
    public TimeSpan? LastRunDuration { get; set; }
    public int LastScoredCount { get; set; }
    public int LastCollectedCount { get; set; }
    public string? LastError { get; set; }
    public int TotalRuns { get; set; }
    public string CurrentPhase { get; set; } = "Idle";
}

public sealed class RunResult
{
    public int CollectedJobs { get; set; }
    public int EvaluatedJobs { get; set; }
    public int HighScoreJobs { get; set; }
    public List<string> Errors { get; set; } = [];
    public TimeSpan Duration { get; set; }
}

public sealed class UpdateJobStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public sealed class TestResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class ProfileAnalysisDto
{
    public string ProfileMarkdown { get; set; } = string.Empty;
    public List<string> IncludeTitleKeywords { get; set; } = [];
    public List<string> ExcludeTitleKeywords { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class CandidateProfileDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProfileMarkdown { get; set; } = string.Empty;
    public List<string> IncludeTitleKeywords { get; set; } = [];
    public List<string> ExcludeTitleKeywords { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class CandidateProfileUpdateRequest
{
    public string Content { get; set; } = string.Empty;
    public List<string> IncludeTitleKeywords { get; set; } = [];
    public List<string> ExcludeTitleKeywords { get; set; } = [];
}

public sealed class CandidateProfileCreateRequest : CandidateProfileUpdateRequest
{
    public string Name { get; set; } = string.Empty;
}
