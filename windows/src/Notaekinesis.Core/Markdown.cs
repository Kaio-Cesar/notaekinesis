using System.Text;
using System.Text.RegularExpressions;

namespace Notaekinesis.Core.Markdown;

// A small Markdown reader for note cards: the common subset the web app renders
// (headings, paragraphs, lists, quotes, code, rules, emphasis, links). Raw HTML is
// shown as text, like the web app (markdown-it with html: false). Single newlines
// are line breaks, as in the web app (breaks: true).

public abstract record Inline;

public sealed record Text(string Value) : Inline;

public sealed record Code(string Value) : Inline;

public sealed record LineBreak : Inline;

public sealed record Strong(IReadOnlyList<Inline> Children) : Inline;

public sealed record Emphasis(IReadOnlyList<Inline> Children) : Inline;

public sealed record Strike(IReadOnlyList<Inline> Children) : Inline;

/// <summary>Only http(s) and mailto links; anything else is kept as text.</summary>
public sealed record Link(IReadOnlyList<Inline> Children, string Url) : Inline;

public abstract record Block;

public sealed record Paragraph(IReadOnlyList<Inline> Inlines) : Block;

public sealed record Heading(int Level, IReadOnlyList<Inline> Inlines) : Block;

public sealed record CodeBlock(string Text) : Block;

public sealed record Quote(IReadOnlyList<Block> Blocks) : Block;

public sealed record Rule : Block;

/// <param name="Depth">0 for top-level items, 1 for items nested under them, and so on.</param>
/// <param name="Marker">"•", "1.", or "☐"/"☑" for task items.</param>
public sealed record ListItem(int Depth, string Marker, IReadOnlyList<Inline> Inlines) : Block;

public static partial class MarkdownParser
{
    [GeneratedRegex(@"^(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$")]
    private static partial Regex HeadingRe();

    [GeneratedRegex(@"^([ \t]*)([-*+]|(\d{1,9})[.)])[ \t]+(?:\[([ xX])\][ \t]+)?(.*)$")]
    private static partial Regex ListRe();

    [GeneratedRegex(@"^[ \t]{0,3}(```|~~~)")]
    private static partial Regex FenceRe();

    [GeneratedRegex(@"^[ \t]{0,3}([-*_])([ \t]*\1){2,}[ \t]*$")]
    private static partial Regex RuleRe();

    [GeneratedRegex(@"^[ \t]{0,3}>[ \t]?")]
    private static partial Regex QuoteRe();

    [GeneratedRegex(@"^\[([^\]\n]*)\]\(([^()\s]+)(?:[ \t]+""[^""]*"")?\)")]
    private static partial Regex LinkRe();

    [GeneratedRegex("""^https?://[^\s<>"'`]+[^\s<>"'`.,;:!?)\]]""", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrlRe();

    [GeneratedRegex(@"^<((?:https?://|mailto:)[^>\s]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex AutolinkRe();

    public static IReadOnlyList<Block> Parse(string markdown) => ParseBlocks(markdown.ReplaceLineEndings("\n").Split('\n'));

    private static List<Block> ParseBlocks(string[] lines)
    {
        var blocks = new List<Block>();
        var para = new List<string>();
        void Flush()
        {
            if (para.Count == 0) return;
            blocks.Add(new Paragraph(ParseInlines(string.Join('\n', para))));
            para.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                Flush();
                continue;
            }

            var fence = FenceRe().Match(line);
            if (fence.Success)
            {
                Flush();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence.Groups[1].Value, StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                blocks.Add(new CodeBlock(string.Join('\n', code)));
                continue;
            }

            if (RuleRe().IsMatch(line) && para.Count == 0)
            {
                blocks.Add(new Rule());
                continue;
            }

            var heading = HeadingRe().Match(line);
            if (heading.Success)
            {
                Flush();
                blocks.Add(new Heading(heading.Groups[1].Length, ParseInlines(heading.Groups[2].Value)));
                continue;
            }

            if (QuoteRe().IsMatch(line))
            {
                Flush();
                var quoted = new List<string>();
                for (; i < lines.Length && QuoteRe().IsMatch(lines[i]); i++) quoted.Add(QuoteRe().Replace(lines[i], "", 1));
                i--;
                blocks.Add(new Quote(ParseBlocks([.. quoted])));
                continue;
            }

            var item = ListRe().Match(line);
            if (item.Success)
            {
                Flush();
                var indent = item.Groups[1].Value.Replace("\t", "    ", StringComparison.Ordinal).Length;
                var marker = item.Groups[4].Success ? (item.Groups[4].Value == " " ? "☐" : "☑")
                    : item.Groups[3].Success ? item.Groups[3].Value + "."
                    : "•";
                var text = new StringBuilder(item.Groups[5].Value);
                // Lazy continuation: following plain lines belong to the item.
                while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !ListRe().IsMatch(lines[i + 1])
                    && !FenceRe().IsMatch(lines[i + 1]) && !HeadingRe().IsMatch(lines[i + 1]) && !QuoteRe().IsMatch(lines[i + 1]))
                    text.Append('\n').Append(lines[++i].Trim());
                blocks.Add(new ListItem(Math.Min(indent / 2, 6), marker, ParseInlines(text.ToString())));
                continue;
            }

            para.Add(line.Trim());
        }
        Flush();
        return blocks;
    }

    public static IReadOnlyList<Inline> ParseInlines(string s)
    {
        var result = new List<Inline>();
        var text = new StringBuilder();
        void FlushText()
        {
            if (text.Length == 0) return;
            result.Add(new Text(text.ToString()));
            text.Clear();
        }
        void Add(Inline inline)
        {
            FlushText();
            result.Add(inline);
        }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var rest = s.AsSpan(i);

            if (c == '\\' && i + 1 < s.Length && char.IsAsciiLetterOrDigit(s[i + 1]) is false && s[i + 1] != '\n')
            {
                text.Append(s[i + 1]);
                i += 2;
                continue;
            }
            if (c == '\n')
            {
                Add(new LineBreak());
                i++;
                continue;
            }
            if (c == '`')
            {
                var ticks = CountRun(s, i, '`');
                var close = s.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                if (close > 0)
                {
                    Add(new Code(s[(i + ticks)..close].Trim()));
                    i = close + ticks;
                    continue;
                }
            }
            if (c == '[' && LinkRe().Match(s[i..]) is { Success: true } link)
            {
                var url = link.Groups[2].Value;
                if (IsSafeUrl(url))
                {
                    Add(new Link(ParseInlines(link.Groups[1].Value), url));
                    i += link.Length;
                    continue;
                }
            }
            if (c == '<' && AutolinkRe().Match(s[i..]) is { Success: true } auto)
            {
                Add(new Link([new Text(auto.Groups[1].Value)], auto.Groups[1].Value));
                i += auto.Length;
                continue;
            }
            if ((c == 'h' || c == 'H') && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && BareUrlRe().Match(s[i..]) is { Success: true } bare)
            {
                Add(new Link([new Text(bare.Value)], bare.Value));
                i += bare.Length;
                continue;
            }
            if ((rest.StartsWith("**") || rest.StartsWith("__") || rest.StartsWith("~~")) && TryDelimited(s, i, 2, out var inner, out var end))
            {
                var children = ParseInlines(inner);
                Add(c == '~' ? new Strike(children) : new Strong(children));
                i = end;
                continue;
            }
            if ((c == '*' || c == '_') && TryDelimited(s, i, 1, out inner, out end))
            {
                Add(new Emphasis(ParseInlines(inner)));
                i = end;
                continue;
            }

            text.Append(c);
            i++;
        }
        FlushText();
        return result;
    }

    private static int CountRun(string s, int at, char c)
    {
        var n = 0;
        while (at + n < s.Length && s[at + n] == c) n++;
        return n;
    }

    /// <summary>Finds the closing delimiter for emphasis at <paramref name="at"/>, following CommonMark's flanking rules loosely.</summary>
    private static bool TryDelimited(string s, int at, int width, out string inner, out int end)
    {
        inner = "";
        end = at;
        var delim = s.Substring(at, width);
        var start = at + width;
        // Opening: not followed by space; `_` must not sit inside a word (snake_case).
        if (start >= s.Length || char.IsWhiteSpace(s[start])) return false;
        if (delim[0] == '_' && at > 0 && char.IsLetterOrDigit(s[at - 1])) return false;
        if (width == 1 && s[start] == delim[0]) return false;

        for (var j = start + 1; j <= s.Length - width; j++)
        {
            if (s[j] == '`')
            {
                // Don't close inside a code span.
                var close = s.IndexOf('`', j + 1);
                if (close > 0) j = close;
                continue;
            }
            if (string.CompareOrdinal(s, j, delim, 0, width) != 0) continue;
            if (char.IsWhiteSpace(s[j - 1])) continue;
            if (width == 1 && ((j + 1 < s.Length && s[j + 1] == delim[0]) || s[j - 1] == delim[0])) continue;
            if (delim[0] == '_' && j + width < s.Length && char.IsLetterOrDigit(s[j + width])) continue;
            inner = s[start..j];
            end = j + width;
            return true;
        }
        return false;
    }

    public static bool IsSafeUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
}
