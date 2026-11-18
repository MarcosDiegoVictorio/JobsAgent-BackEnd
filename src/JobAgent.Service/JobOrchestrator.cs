using System.Collections.Concurrent;
using System.Diagnostics;
using JobAgent.Domain.Common;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobAgent.Service;

public sealed class JobOrchestrator(
    IEnumerable<IJobSource> sources,
    IJobRepository repo,
    IUserRepository users,
    IJobScorer scorer,
    IProfileService profileService,
    IOptionsMonitor<AgentOptions> options,
    ILogger<JobOrchestrator> log) : IJobOrchestrator
{
    private const int MaxConcurrentEvaluations = 2;
    private const int MaxSavedJobsPerUser = 10;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> runLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RunStatus> statuses = new(StringComparer.Ordinal);

    public RunStatus GetCurrentStatus(string userId)
    {
        if (!statuses.TryGetValue(userId, out var status))
        {
            return new RunStatus();
        }

        lock (status)
        {
            return new RunStatus
            {
                IsRunning = status.IsRunning,
                LastRunTime = status.LastRunTime,
                LastRunDuration = status.LastRunDuration,
                LastScoredCount = status.LastScoredCount,
                LastCollectedCount = status.LastCollectedCount,
                LastError = status.LastError,
                TotalRuns = status.TotalRuns,
                CurrentPhase = status.CurrentPhase
            };
        }
    }

    public async Task<RunResult> RunOnceAsync(string userId, string runId, CancellationToken ct = default)
    {
        var runLock = runLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        if (!await runLock.WaitAsync(0, ct))
        {
            var concurrentResult = new RunResult { Errors = ["Já existe uma busca em andamento."] };
            await users.CompleteSearchAsync(runId, concurrentResult, ct);
            return concurrentResult;
        }

        var status = statuses.GetOrAdd(userId, _ => new RunStatus());
        var stopwatch = Stopwatch.StartNew();
        var result = new RunResult();
        lock (status)
        {
            status.IsRunning = true;
            status.CurrentPhase = "Preparando busca...";
            status.LastError = null;
        }

        try
        {
            var profile = await profileService.GetActiveProfileAsync(userId, ct);
            if (string.IsNullOrWhiteSpace(profile.ProfileMarkdown))
            {
                result.Errors.Add("Preencha seu perfil antes de iniciar uma busca.");
                return result;
            }

            var agentOptions = options.CurrentValue;
            var searchContext = new JobSearchContext
            {
                UserId = userId,
                ProfileId = profile.Id,
                Keywords = profile.IncludeTitleKeywords
            };
            var seenThisRun = new HashSet<string>();
            var candidateJobs = new List<JobPosting>();
            var collectedCount = 0;

            foreach (var source in sources)
            {
                lock (status)
                {
                    status.CurrentPhase = $"Buscando vagas da fonte: {source.Name}...";
                }

                IReadOnlyList<JobPosting> jobs;
                try
                {
                    jobs = await source.FetchAsync(searchContext, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Fonte {Source} falhou durante coleta para usuário {UserId}", source.Name, userId);
                    result.Errors.Add($"Fonte {source.Name} falhou durante a busca.");
                    continue;
                }

                log.LogInformation("Fonte {Source}: {Count} vagas encontradas", source.Name, jobs.Count);
                collectedCount += jobs.Count;
                foreach (var job in jobs)
                {
                    var scopedJob = job with { Id = TextHelper.IdFor($"{userId}:{profile.Id}:{job.Url}") };
                    if (seenThisRun.Add(scopedJob.Id)
                        && PassesFilter(scopedJob, profile.IncludeTitleKeywords, profile.ExcludeTitleKeywords))
                    {
                        candidateJobs.Add(scopedJob);
                    }
                }
            }

            result.CollectedJobs = collectedCount;
            var slotsRemaining = Math.Max(0, MaxSavedJobsPerUser - await repo.GetCountAsync(userId, ct));
            var jobsToScore = new List<JobPosting>();
            foreach (var job in candidateJobs
                         .OrderByDescending(GetRelevancePriority)
                         .ThenByDescending(item => item.PublishedAt ?? DateTimeOffset.MinValue))
            {
                if (jobsToScore.Count >= Math.Min(slotsRemaining, Math.Max(0, agentOptions.MaxJobsPerRun)))
                {
                    break;
                }

                if (!await repo.ExistsAsync(job.Id, userId, profile.Id, ct))
                {
                    jobsToScore.Add(job);
                }
            }

            for (var batchStart = 0; batchStart < jobsToScore.Count; batchStart += MaxConcurrentEvaluations)
            {
                var batchSize = Math.Min(MaxConcurrentEvaluations, jobsToScore.Count - batchStart);
                var scores = new JobScore?[batchSize];
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, batchSize),
                    new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentEvaluations, CancellationToken = ct },
                    async (batchIndex, token) =>
                    {
                        var job = jobsToScore[batchStart + batchIndex];
                        lock (status)
                        {
                            status.CurrentPhase = $"Avaliando vaga: {job.Title}...";
                        }

                        scores[batchIndex] = await scorer.ScoreAsync(job, profile.ProfileMarkdown, token);
                    });

                for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
                {
                    var score = scores[batchIndex];
                    if (score is null)
                    {
                        continue;
                    }

                    var job = jobsToScore[batchStart + batchIndex];
                    if (await repo.SaveAsync(job, score, userId, profile.Id, ct))
                    {
                        result.EvaluatedJobs++;
                        if (score.Score >= agentOptions.MinScoreToNotify)
                        {
                            result.HighScoreJobs++;
                        }
                    }
                }
            }

            if (slotsRemaining == 0)
            {
                result.Errors.Add("O limite de 10 vagas salvas foi atingido.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falha inesperada na busca do usuário {UserId}", userId);
            result.Errors.Add("Ocorreu uma falha inesperada durante a busca.");
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            lock (status)
            {
                status.IsRunning = false;
                status.LastRunTime = DateTimeOffset.UtcNow;
                status.LastRunDuration = stopwatch.Elapsed;
                status.LastCollectedCount = result.CollectedJobs;
                status.LastScoredCount = result.EvaluatedJobs;
                status.TotalRuns++;
                status.LastError = result.Errors.Count == 0 ? null : string.Join(" ", result.Errors);
                status.CurrentPhase = "Concluído";
            }

            try
            {
                await users.CompleteSearchAsync(runId, result, CancellationToken.None);
            }
            finally
            {
                runLock.Release();
            }
        }

        return result;
    }

    private static bool PassesFilter(
        JobPosting job,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords)
    {
        var haystack = $"{job.Title} {job.Description}";
        if (excludeKeywords.Any(keyword =>
                !string.IsNullOrWhiteSpace(keyword)
                && job.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return includeKeywords.Count == 0
            || includeKeywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static int GetRelevancePriority(JobPosting job)
    {
        var title = job.Title;
        if (title.Contains(".net", StringComparison.OrdinalIgnoreCase)
            || title.Contains("dotnet", StringComparison.OrdinalIgnoreCase)
            || title.Contains("c#", StringComparison.OrdinalIgnoreCase)
            || title.Contains("c sharp", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (title.Contains("analista de sistemas", StringComparison.OrdinalIgnoreCase)
            || title.Contains("analista de desenvolvimento", StringComparison.OrdinalIgnoreCase)
            || title.Contains("analista programador", StringComparison.OrdinalIgnoreCase)
            || title.Contains("analista desenvolvedor", StringComparison.OrdinalIgnoreCase)
            || title.Contains("system analyst", StringComparison.OrdinalIgnoreCase)
            || title.Contains("systems analyst", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var description = job.Description;
        return description.Contains(".net", StringComparison.OrdinalIgnoreCase)
               || description.Contains("dotnet", StringComparison.OrdinalIgnoreCase)
               || description.Contains("c#", StringComparison.OrdinalIgnoreCase)
               || description.Contains("c sharp", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }
}
