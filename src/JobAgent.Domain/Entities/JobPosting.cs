namespace JobAgent.Domain.Entities;

public sealed record JobPosting(
    string Id,
    string Source,
    string Title,
    string Company,
    string Url,
    string Description,
    DateTimeOffset? PublishedAt);
