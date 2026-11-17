using Notaekinesis.Core.Markdown;

namespace Notaekinesis.Core.Tests;

public sealed class MarkdownTests
{
    /// <summary>A compact form that's easy to compare: text as-is, **b**, *i*, ~~s~~, `c`, [text](url), / for breaks.</summary>
    private static string Show(IEnumerable<Inline> inlines) =>
        string.Concat(
            inlines.Select(i =>
                i switch
                {
                    Text t => t.Value,
                    Code c => $"`{c.Value}`",
                    LineBreak => "/",
                    Strong s => $"**{Show(s.Children)}**",
                    Emphasis e => $"*{Show(e.Children)}*",
                    Strike s => $"~~{Show(s.Children)}~~",
                    Link l => $"[{Show(l.Children)}]({l.Url})",
                    _ => "?",
                }
            )
        );

    private static string Show(Block block) =>
        block switch
        {
            Paragraph p => "p:" + Show(p.Inlines),
            Heading h => $"h{h.Level}:" + Show(h.Inlines),
            ListItem l => $"li{l.Depth}{l.Marker}:" + Show(l.Inlines),
            CodeBlock c => "code:" + c.Text,
            Quote q => "quote:" + string.Join("|", q.Blocks.Select(Show)),
            Rule => "hr",
            _ => "?",
        };

    private static string Inl(string s) => Show(MarkdownParser.ParseInlines(s));

    [Fact]
    public void Emphasis_code_and_links()
    {
        Assert.Equal("a **bold** and *it* `x*y*`", Inl("a __bold__ and _it_ `x*y*`"));
        Assert.Equal("**a *b* c**", Inl("**a *b* c**"));
        Assert.Equal("[the docs](https://bun.sh/docs)", Inl("[the docs](https://bun.sh/docs)"));
        Assert.Equal("~~old~~", Inl("~~old~~"));
        Assert.Equal("[https://x.dev](https://x.dev)", Inl("<https://x.dev>"));
    }

    [Fact]
    public void Bare_urls_become_links_without_trailing_punctuation() =>
        Assert.Equal("see [https://example.com/a_b](https://example.com/a_b), then", Inl("see https://example.com/a_b, then"));

    [Fact]
    public void Unsafe_links_html_and_snake_case_stay_text()
    {
        Assert.Equal("[x](javascript:alert(1))", Inl("[x](javascript:alert(1))"));
        Assert.Equal("snake_case_name and a * b * c", Inl("snake_case_name and a * b * c"));
        Assert.Equal("<b>not html</b>", Inl("<b>not html</b>"));
        Assert.Equal("*literal*", Inl(@"\*literal\*"));
    }

    [Fact]
    public void Single_newlines_are_line_breaks() =>
        Assert.Equal(["p:one/two"], MarkdownParser.Parse("one\ntwo").Select(Show));

    [Fact]
    public void Blocks() =>
        Assert.Equal(
            [
                "h1:Plan",
                "li0•:step one",
                "li2•:nested",
                "li01.:first",
                "li0☐:todo",
                "li0☑:done",
                "code:#include <stdio.h>\n\nint x;",
                "quote:p:quoted *text*",
                "hr",
                "p:end",
            ],
            MarkdownParser
                .Parse("# Plan\n\n- step one\n    - nested\n1. first\n- [ ] todo\n- [x] done\n\n```c\n#include <stdio.h>\n\nint x;\n```\n> quoted *text*\n\n---\nend")
                .Select(Show)
        );

    [Fact]
    public void An_unclosed_fence_runs_to_the_end() =>
        Assert.Equal(["code:code\nmore"], MarkdownParser.Parse("```\ncode\nmore").Select(Show));
}
