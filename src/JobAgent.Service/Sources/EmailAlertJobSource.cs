using System.Net;
using System.Text.RegularExpressions;
using JobAgent.Domain.Common;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobAgent.Service.Sources;

public sealed partial class EmailAlertJobSource(
    IOptionsMonitor<EmailOptions> options,
    ILogger<EmailAlertJobSource> log) : IJobSource
{
    public string Name => "email";

    [GeneratedRegex("""<a\s[^>]*href=["']([^"']+)["'][^>]*>(.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnchorRx();

    public async Task<IReadOnlyList<JobPosting>> FetchAsync(JobSearchContext context, CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        var jobs = new List<JobPosting>();
        if (!o.Enabled || string.IsNullOrWhiteSpace(o.User) || string.IsNullOrWhiteSpace(o.AppPassword))
        {
            return jobs;
        }

        try
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(o.Host, o.Port, SecureSocketOptions.SslOnConnect, ct);
            await imap.AuthenticateAsync(o.User, o.AppPassword, ct);

            var folder = o.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
                ? imap.Inbox
                : await imap.GetFolderAsync(o.Folder, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);

            var since = DateTime.UtcNow.AddDays(-o.MaxAgeDays);
            var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(since), ct);

            foreach (var uid in uids.TakeLast(50))
            {
                var msg = await folder.GetMessageAsync(uid, ct);
                var from = msg.From.Mailboxes.FirstOrDefault()?.Address ?? "";
                if (o.FromContains.Count > 0 &&
                    !o.FromContains.Any(f => from.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    continue;

                foreach (Match m in AnchorRx().Matches(msg.HtmlBody ?? ""))
                {
                    var href = WebUtility.HtmlDecode(m.Groups[1].Value);
                    var text = TextHelper.StripHtml(m.Groups[2].Value);

                    if (text.Length < 8) continue;
                    if (o.LinkContains.Count > 0 && !o.LinkContains.Any(l => href.Contains(l, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var url = TextHelper.CleanUrl(href);
                    jobs.Add(new JobPosting(
                        Id: TextHelper.IdFor(url),
                        Source: $"email:{from}",
                        Title: TextHelper.Truncate(text, 200),
                        Company: "",
                        Url: url,
                        Description: text,
                        PublishedAt: msg.Date));
                }
            }

            await imap.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Falha ao ler os alertas por e-mail via IMAP");
        }

        return jobs;
    }
}
