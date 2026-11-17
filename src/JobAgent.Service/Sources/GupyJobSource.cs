using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using JobAgent.Domain.Common;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace JobAgent.Service.Sources;

public sealed class GupyJobSource(
    IHttpClientFactory httpFactory,
    ILogger<GupyJobSource> log) : IJobSource
{
    private const int MaxSearchTerms = 5;
    private const int MaxConcurrentSearches = 3;
    private const int PageSize = 20;
    private const string SearchEndpoint = "https://portal.gupy.io/api/job-search/jobs";

    public string Name => "Gupy";

    public async Task<IReadOnlyList<JobPosting>> FetchAsync(JobSearchContext context, CancellationToken ct = default)
    {
        var terms = context.Keywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSearchTerms)
            .ToList();

        if (terms.Count == 0)
        {
            terms.Add("desenvolvedor");
        }

        var jobs = new ConcurrentBag<JobPosting>();
        using var limit = new SemaphoreSlim(MaxConcurrentSearches);

        async Task FetchTermAsync(string term)
        {
            await limit.WaitAsync(ct);
            try
            {
                var url = $"{SearchEndpoint}?jobName={Uri.EscapeDataString(term)}" +
                          $"&limit={PageSize}&offset=0&sortBy=publishedDate&sortOrder=desc";
                var client = httpFactory.CreateClient("Gupy");
                using var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("Gupy retornou HTTP {StatusCode} durante a busca.", (int)response.StatusCode);
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    log.LogWarning("A resposta pública de busca da Gupy não continha uma lista de vagas.");
                    return;
                }

                foreach (var item in data.EnumerateArray())
                {
                    var title = ReadString(item, "name");
                    var jobUrl = ReadString(item, "jobUrl");
                    if (string.IsNullOrWhiteSpace(title)
                        || !Uri.TryCreate(jobUrl, UriKind.Absolute, out var uri)
                        || uri.Scheme is not ("http" or "https"))
                    {
                        continue;
                    }

                    var cleanUrl = TextHelper.CleanUrl(jobUrl);
                    var description = TextHelper.Truncate(
                        TextHelper.StripHtml(ReadString(item, "description")),
                        20_000);
                    var publishedAt = DateTimeOffset.TryParse(
                        ReadString(item, "publishedDate"),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var published)
                        ? published
                        : (DateTimeOffset?)null;

                    jobs.Add(new JobPosting(
                        Id: TextHelper.IdFor(cleanUrl),
                        Source: Name,
                        Title: TextHelper.Truncate(title, 200),
                        Company: ReadString(item, "careerPageName"),
                        Url: cleanUrl,
                        Description: description,
                        PublishedAt: publishedAt));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Falha ao consultar a busca pública da Gupy.");
            }
            finally
            {
                limit.Release();
            }
        }

        await Task.WhenAll(terms.Select(FetchTermAsync));
        return jobs
            .GroupBy(job => job.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string ReadString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
