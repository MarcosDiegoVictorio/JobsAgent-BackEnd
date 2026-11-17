using System.Text.Json.Serialization;

namespace JobAgent.Domain.Entities;

public sealed class JobScore
{
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("strengths")] public List<string> Strengths { get; set; } = [];
    [JsonPropertyName("gaps")] public List<string> Gaps { get; set; } = [];
    [JsonPropertyName("cv_tips")] public string CvTips { get; set; } = string.Empty;
    [JsonPropertyName("recruiter_message")] public string RecruiterMessage { get; set; } = string.Empty;
}
