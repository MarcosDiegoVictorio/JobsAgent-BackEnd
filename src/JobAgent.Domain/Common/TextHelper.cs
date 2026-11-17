using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JobAgent.Domain.Common;

public static partial class TextHelper
{
    [GeneratedRegex("<script.*?</script>|<style.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRx();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRx();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRx();

    public static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var s = ScriptStyleRx().Replace(html, " ");
        s = TagRx().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        return WhitespaceRx().Replace(s, " ").Trim();
    }

    /// <summary>Remove parâmetros de rastreio para que a mesma vaga gere sempre o mesmo Id.</summary>
    public static string CleanUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;

        var baseUrl = $"{u.Scheme}://{u.Host}{u.AbsolutePath}".TrimEnd('/');
        if (u.Host.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase))
            return baseUrl.Replace("/comm/jobs/", "/jobs/");

        var kept = u.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
                     && !p.StartsWith("trk", StringComparison.OrdinalIgnoreCase)
                     && !p.StartsWith("ref", StringComparison.OrdinalIgnoreCase));
        var query = string.Join('&', kept);
        return query.Length == 0 ? baseUrl : $"{baseUrl}?{query}";
    }

    public static string IdFor(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.ToLowerInvariant())))[..16];

    public static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max] + "…";
    }
}
