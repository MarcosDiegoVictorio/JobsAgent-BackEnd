using System.ServiceModel.Syndication;
using System.Xml;
using JobAgent.Domain.Common;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobAgent.Service.Sources;

public sealed class RssJobSource(
    IOptionsMonitor<RssOptions> options,
    IHttpClientFactory httpFactory,
    ILogger<RssJobSource> log) : IJobSource
{
    private const int MaxConcurrentFeeds = 4;

    public string Name => "rss";

    public async Task<IReadOnlyList<JobPosting>> FetchAsync(JobSearchContext context, CancellationToken ct = default)
    {
        var feeds = options.CurrentValue.Feeds.Where(f => !string.IsNullOrWhiteSpace(f.Url)).ToList();

        if (feeds.Count == 0)
        {
            return [];
        }

        var client = httpFactory.CreateClient("Rss");
        client.Timeout = TimeSpan.FromSeconds(30);
        using var feedLimit = new SemaphoreSlim(MaxConcurrentFeeds);

        async Task<List<JobPosting>> FetchFeedAsync(RssFeed feed)
        {
            await feedLimit.WaitAsync(ct);
            try
            {
                await using var stream = await client.GetStreamAsync(feed.Url, ct);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit
                });
                var syndication = SyndicationFeed.Load(reader);
                var jobs = new List<JobPosting>();

                foreach (var item in syndication.Items)
                {
                    var link = item.Links.FirstOrDefault()?.Uri?.ToString()
                               ?? item.Links.FirstOrDefault()?.Title;
                    if (string.IsNullOrWhiteSpace(link)) continue;

                    var url = TextHelper.CleanUrl(link);
                    jobs.Add(new JobPosting(
                        Id: TextHelper.IdFor(url),
                        Source: $"rss:{feed.Name}",
                        Title: item.Title?.Text ?? "(sem título)",
                        Company: item.Authors.FirstOrDefault()?.Name ?? feed.Name,
                        Url: url,
                        Description: TextHelper.StripHtml(item.Summary?.Text ?? ""),
                        PublishedAt: item.PublishDate == default ? null : item.PublishDate));
                }

                return jobs;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Falha ao ler o feed RSS {Feed}", feed.Name);
                return [];
            }
            finally
            {
                feedLimit.Release();
            }
        }

        var feedJobs = await Task.WhenAll(feeds.Select(FetchFeedAsync));
        return feedJobs.SelectMany(jobs => jobs).ToList();
    }
}
