using System.Net;
using System.Text.RegularExpressions;

namespace Notaekinesis.Core;

/// <summary>
/// Turns what was dragged or pasted onto the basket into an item. Unlike the quick-add box,
/// dropped text is saved as it is: a '#' in someone else's text is not meant as a tag.
/// </summary>
public static partial class DropReader
{
    [GeneratedRegex("""^https?://[^\s<>"'`]+$""", RegexOptions.IgnoreCase)]
    private static partial Regex SingleUrl();

    [GeneratedRegex("""<a\b[^>]*?\bhref\s*=\s*(?:"([^"]*)"|'([^']*)')[^>]*>(.*?)</a\s*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    /// <summary>Extensions the server accepts (it checks the bytes; this is for early feedback).</summary>
    public static readonly IReadOnlySet<string> FileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".mp4", ".m4v", ".webm", ".mov", ".pdf",
    };

    public static bool IsSupportedFile(string path) => FileExtensions.Contains(Path.GetExtension(path));

    /// <param name="link">A real link (a dragged hyperlink, tab or address bar), if the drag carried one.</param>
    /// <param name="html">The HTML fragment that came with it, used to find the link's title.</param>
    /// <param name="text">Plain text.</param>
    public static Capture? Read(string? link, string? html, string? text)
    {
        link = link?.Trim();
        if (link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            return new Capture("", link, [], TitleFor(link, html, text));

        var plain = text?.Trim();
        if (string.IsNullOrEmpty(plain)) return null;
        if (SingleUrl().IsMatch(plain)) return new Capture("", plain, [], TitleFor(plain, html, null));
        return new Capture(plain, null, []);
    }

    /// <summary>The text of the dragged anchor, or failing that, a short one-line text that isn't the URL itself.</summary>
    internal static string? TitleFor(string url, string? html, string? text)
    {
        if (!string.IsNullOrEmpty(html))
        {
            foreach (Match a in Anchor().Matches(html))
            {
                var href = WebUtility.HtmlDecode(a.Groups[1].Success ? a.Groups[1].Value : a.Groups[2].Value);
                if (!SameUrl(href, url)) continue;
                var title = Clean(WebUtility.HtmlDecode(Tag().Replace(a.Groups[3].Value, " ")));
                if (title is not null && !SameUrl(title, url)) return title;
            }
        }
        var line = Clean(text);
        return line is not null && line.Length <= 300 && !line.Contains("://", StringComparison.Ordinal) ? line : null;
    }

    private static string? Clean(string? s)
    {
        if (s is null) return null;
        var t = Space().Replace(s, " ").Trim();
        return t.Length == 0 ? null : t;
    }

    private static bool SameUrl(string a, string b) => string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
