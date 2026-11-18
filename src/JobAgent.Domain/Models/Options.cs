namespace JobAgent.Domain.Models;

public sealed class AgentOptions
{
    public int RunEveryMinutes { get; set; } = 360;
    public int MinScoreToNotify { get; set; } = 7;
    public int MaxJobsPerRun { get; set; } = 40;
    public string CandidateProfilePath { get; set; } = "profile/candidate.md";
    public string DatabasePath { get; set; } = "jobs.db";
    public bool EnableBackgroundWorker { get; set; } = true;
    public string LlmProvider { get; set; } = "Gemini"; // "Gemini" ou "Anthropic"
    public List<string> IncludeTitleKeywords { get; set; } = [];
    public List<string> ExcludeTitleKeywords { get; set; } = [];
}

public sealed class AuthOptions
{
    public string GoogleClientId { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    public string JwtSigningKey { get; set; } = "";
    public string Issuer { get; set; } = "JobAgent";
    public string Audience { get; set; } = "JobAgent.Client";
    public int TokenLifetimeMinutes { get; set; } = 30;
    public string FrontendUrl { get; set; } = "";
}

public sealed class DatabaseOptions
{
    public string Provider { get; set; } = "SQLite";
    public string SqlitePath { get; set; } = "jobs.db";
    public string ConnectionString { get; set; } = "";
}

public sealed class GeminiOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.5-flash";
}

public sealed class AnthropicOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-5-5";
    public int MaxTokens { get; set; } = 1500;
}

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
}

public sealed class RssOptions
{
    public List<RssFeed> Feeds { get; set; } = [];
}

public sealed class RssFeed
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "imap.gmail.com";
    public int Port { get; set; } = 993;
    public string User { get; set; } = "";
    public string AppPassword { get; set; } = "";
    public string Folder { get; set; } = "INBOX";
    public int MaxAgeDays { get; set; } = 3;
    public List<string> FromContains { get; set; } = [];
    public List<string> LinkContains { get; set; } = [];
}
