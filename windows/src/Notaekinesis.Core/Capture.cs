using System.Text.RegularExpressions;

namespace Notaekinesis.Core;

/// <summary>What the quick-add box and drag and drop turn into an item. Text is Markdown.</summary>
public sealed record Capture(string Text, string? Url, IReadOnlyList<string> Tags);

/// <summary>A port of web/src/lib/capture.ts; keep the two in step (the tests mirror tests/capture.test.ts).</summary>
public static partial class CaptureParser
{
    [GeneratedRegex("""\bhttps?://[^\s<>"'`]+[^\s<>"'`.,;:!?)\]]""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRe();

    // `#tag` at the start of a line or after a space, followed by a space or the end of the line.
    // `# Heading` (with a space) is a Markdown heading and doesn't match.
    [GeneratedRegex(@"(?<=^|[ \t])#([\p{L}\p{N}_-]{1,64})(?=[ \t]|$)", RegexOptions.Multiline)]
    private static partial Regex TagRe();

    // Parts of Markdown that must be left untouched: fenced code, inline code, links and autolinks.
    [GeneratedRegex(@"(^|\n)(```|~~~)[\s\S]*?(\n\2[^\n]*(?=\n|\z)|\z)|`[^`\n]+`|!?\[[^\]\n]*\]\([^)\n]*\)|<https?:[^>\s]+>")]
    private static partial Regex ProtectedRe();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex Gaps();

    private static List<(int Start, int End)> ProtectedRanges(string text) =>
        [.. ProtectedRe().Matches(text).Select(m => (m.Index, m.Index + m.Length))];

    private static bool Inside(List<(int Start, int End)> ranges, int start, int end) =>
        ranges.Exists(r => start < r.End && end > r.Start);

    /// <summary>Removes text[start, end) without disturbing the rest of the Markdown.</summary>
    private static string Cut(string text, int start, int end)
    {
        var before = text[..start];
        var after = text[end..];
        var atLineStart = before.Length == 0 || before.EndsWith('\n');
        if (atLineStart) after = after.TrimStart(' ', '\t');
        else if (after.Length == 0 || after[0] is ' ' or '\t' or '\n') before = before.TrimEnd(' ', '\t');
        return before + after;
    }

    /// <summary>
    /// Parses free text the way a chat box would, keeping it valid Markdown:
    /// <c>#tags</c> become tags; a bare URL becomes the link when it's a one-liner or sits
    /// alone on the first line; everything else stays in the note. Null if there's nothing to save.
    /// </summary>
    public static Capture? Parse(string input)
    {
        var text = input.ReplaceLineEndings("\n").Trim();
        if (text.Length == 0) return null;

        var tags = new List<string>();
        var ranges = ProtectedRanges(text);
        foreach (var m in TagRe().Matches(text).Reverse())
        {
            if (Inside(ranges, m.Index, m.Index + m.Length)) continue;
            tags.Insert(0, m.Groups[1].Value.ToLowerInvariant());
            text = Cut(text, m.Index, m.Index + m.Length);
        }
        text = Gaps().Replace(text, "\n\n").Trim();

        string? url = null;
        ranges = ProtectedRanges(text);
        var bare = UrlRe().Matches(text).Where(m => !Inside(ranges, m.Index, m.Index + m.Length)).ToList();
        if (bare.Count > 0)
        {
            var first = bare[0];
            var firstLine = text.Split('\n', 2)[0];
            var oneLiner = !text.Contains('\n') && bare.Count == 1;
            var aloneOnFirstLine = first.Index == 0 && firstLine.Trim() == first.Value;
            if (oneLiner || aloneOnFirstLine)
            {
                url = first.Value;
                text = Cut(text, first.Index, first.Index + first.Length).Trim();
            }
        }

        if (text.Length == 0 && url is null) return null;
        return new Capture(text, url, tags.Distinct().ToList());
    }

    /// <summary>Turns an item back into the text <see cref="Parse"/> reads, for editing.</summary>
    public static string ToText(Item item)
    {
        var tags = string.Join(' ', item.Tags.Select(t => "#" + t));
        return string.Join("\n\n", new[] { item.Url, item.Text, tags }.Where(s => !string.IsNullOrEmpty(s)));
    }
}
