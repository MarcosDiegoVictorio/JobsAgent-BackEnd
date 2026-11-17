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
    IJobScorer scorer,
    ITelegramNotifier notifier,
    IProfileService profileService,
    IOptionsMonitor<AgentOptions> options,
    ILogger<JobOrchestrator> log) : IJobOrchestrator
{
    private const int MaxConcurrentEvaluations = 2;
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static readonly RunStatus Status = new();

    public RunStatus GetCurrentStatus()
    {
        lock (Status)
        {
            return new RunStatus
            {
                IsRunning = Status.IsRunning,
                LastRunTime = Status.LastRunTime,
                LastRunDuration = Status.LastRunDuration,
                LastScoredCount = Status.LastScoredCount,
                LastCollectedCount = Status.LastCollectedCount,
                LastError = Status.LastError,
                TotalRuns = Status.TotalRuns,
                CurrentPhase = Status.CurrentPhase
            };
        }
    }

    public async Task<RunResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (!await RunLock.WaitAsync(0, ct))
        {
            log.LogInformation("Um ciclo de coleta/avaliação já está em andamento.");
            return new RunResult
            {
                Errors = ["Um ciclo já está em execução."]
            };
        }

        var sw = Stopwatch.StartNew();
        var result = new RunResult();

        lock (Status)
        {
            Status.IsRunning = true;
            Status.CurrentPhase = "Iniciando ciclo e preparando banco de dados...";
            Status.LastError = null;
        }

        try
        {
            await repo.InitAsync(ct);

            var o = options.CurrentValue;
            var profile = await profileService.GetActiveProfileAsync(ct);
            var candidateProfile = profile.ProfileMarkdown;
            var searchContext = new JobSearchContext
            {
                ProfileId = profile.Id,
                Keywords = profile.IncludeTitleKeywords
            };

            if (string.IsNullOrWhiteSpace(candidateProfile))
            {
                var msg = "Perfil do candidato está vazio ou não foi encontrado.";
                log.LogWarning(msg);
                result.Errors.Add(msg);
            }

            var seenThisRun = new HashSet<string>();
            var candidateJobs = new List<JobPosting>();
            var collectedCount = 0;

            foreach (var source in sources)
            {
                lock (Status)
                {
                    Status.CurrentPhase = $"Buscando vagas da fonte: {source.Name}...";
                }

                IReadOnlyList<JobPosting> jobs;
                try
                {
                    jobs = await source.FetchAsync(searchContext, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Fonte {Source} falhou durante coleta", source.Name);
                    result.Errors.Add($"Fonte {source.Name} falhou: {ex.Message}");
                    continue;
                }

                log.LogInformation("Fonte {Source}: {Count} vagas encontradas", source.Name, jobs.Count);
                collectedCount += jobs.Count;

                foreach (var job in jobs)
                {
                    var profileJob = profile.Id == "default"
                        ? job
                        : job with { Id = TextHelper.IdFor($"{profile.Id}:{job.Url}") };

                    if (!seenThisRun.Add(profileJob.Id))
                        continue;

                    if (!PassesFilter(profileJob, profile.IncludeTitleKeywords, profile.ExcludeTitleKeywords))
                        continue;

                    candidateJobs.Add(profileJob);
                }
            }

            var jobsToScore = new List<JobPosting>();
            foreach (var job in candidateJobs
                         .OrderByDescending(GetRelevancePriority)
                         .ThenByDescending(job => job.PublishedAt ?? DateTimeOffset.MinValue))
            {
                if (jobsToScore.Count >= o.MaxJobsPerRun)
                {
                    log.LogInformation("Limite máximo de {Max} avaliações por ciclo atingido.", o.MaxJobsPerRun);
                    break;
                }

                if (!await repo.ExistsAsync(job.Id, profile.Id, ct))
                {
                    jobsToScore.Add(job);
                }
            }

            var scoredCount = 0;
            for (var batchStart = 0; batchStart < jobsToScore.Count; batchStart += MaxConcurrentEvaluations)
            {
                var batchSize = Math.Min(MaxConcurrentEvaluations, jobsToScore.Count - batchStart);
                var scores = new JobScore?[batchSize];
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, batchSize),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxConcurrentEvaluations,
                        CancellationToken = ct
                    },
                    async (batchIndex, token) =>
                    {
                        var job = jobsToScore[batchStart + batchIndex];
                        lock (Status)
                        {
                            Status.CurrentPhase = $"Avaliando vaga com IA: {job.Title}...";
                        }

                        scores[batchIndex] = await scorer.ScoreAsync(job, candidateProfile, token);
                    });

                for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
                {
                    var score = scores[batchIndex];
                    if (score is null)
                        continue;

                    var job = jobsToScore[batchStart + batchIndex];
                    await repo.SaveAsync(job, score, profile.Id, ct);
                    scoredCount++;
                    log.LogInformation("Vaga avaliada: {Score}/10 — {Title} ({Company})", score.Score, job.Title, job.Company);

                    if (score.Score >= o.MinScoreToNotify)
                    {
                        result.HighScoreJobs++;
                        await notifier.SendAsync(job, score, ct);
                    }
                }
            }

            result.CollectedJobs = collectedCount;
            result.EvaluatedJobs = scoredCount;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Erro inesperado na execução do ciclo de busca");
            result.Errors.Add(ex.Message);
            lock (Status)
            {
                Status.LastError = ex.Message;
            }
        }
        finally
        {
            sw.Stop();
            result.Duration = sw.Elapsed;

            lock (Status)
            {
                Status.IsRunning = false;
                Status.LastRunTime = DateTimeOffset.UtcNow;
                Status.LastRunDuration = sw.Elapsed;
                Status.LastCollectedCount = result.CollectedJobs;
                Status.LastScoredCount = result.EvaluatedJobs;
                Status.TotalRuns++;
                Status.CurrentPhase = "Concluído";
            }

            RunLock.Release();
        }

        return result;
    }

    private static bool PassesFilter(
        JobPosting job,
        IReadOnlyCollection<string> includeKeywords,
        IReadOnlyCollection<string> excludeKeywords)
    {
        var haystack = $"{job.Title} {job.Description}";

        if (excludeKeywords.Any(k => !string.IsNullOrWhiteSpace(k) && job.Title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return false;

        return includeKeywords.Count == 0
            || includeKeywords.Where(k => !string.IsNullOrWhiteSpace(k))
                .Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));
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
