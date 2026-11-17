using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobAgent.Service;

public sealed class LlmJobScorer(
    GeminiScorer geminiScorer,
    ClaudeScorer claudeScorer,
    IOptionsMonitor<AgentOptions> agentOptions,
    ILogger<LlmJobScorer> log) : IJobScorer
{
    private IJobScorer ActiveScorer
    {
        get
        {
            var provider = agentOptions.CurrentValue.LlmProvider?.Trim() ?? "Gemini";
            if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
            {
                return claudeScorer;
            }

            return geminiScorer;
        }
    }

    public Task<JobScore?> ScoreAsync(JobPosting job, string candidateProfile, CancellationToken ct = default)
    {
        var provider = agentOptions.CurrentValue.LlmProvider?.Trim() ?? "Gemini";
        log.LogInformation("Avaliando vaga com provedor de IA: {Provider}", provider);
        return ActiveScorer.ScoreAsync(job, candidateProfile, ct);
    }

    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        return ActiveScorer.TestConnectionAsync(ct);
    }

    public Task<(bool Success, string Message)> TestGeminiAsync(CancellationToken ct = default)
    {
        return geminiScorer.TestConnectionAsync(ct);
    }

    public Task<(bool Success, string Message)> TestAnthropicAsync(CancellationToken ct = default)
    {
        return claudeScorer.TestConnectionAsync(ct);
    }
}
