using Notaekinesis.Core;

namespace Notaekinesis.Core.Tests;

/// <summary>Mirrors tests/capture.test.ts so both clients read quick-add text the same way.</summary>
public sealed class CaptureTests
{
    private static void Same(Capture? expected, Capture? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected!.Text, actual.Text);
        Assert.Equal(expected.Url, actual.Url);
        Assert.Equal(expected.Tags, actual.Tags);
    }

    [Fact]
    public void A_bare_url_becomes_a_link_with_no_text() =>
        Same(new Capture("", "https://bun.sh/docs", []), CaptureParser.Parse("  https://bun.sh/docs  "));

    [Fact]
    public void Plain_text_stays_a_note() =>
        Same(new Capture("buy milk\nand eggs", null, []), CaptureParser.Parse("buy milk\r\nand eggs"));

    [Fact]
    public void Tags_are_pulled_out_and_lower_cased() =>
        Same(new Capture("Read this later", null, ["reading", "dev"]), CaptureParser.Parse("Read this later #Reading #dev #reading"));

    [Fact]
    public void A_url_with_a_comment_becomes_a_link_with_text() =>
        Same(
            new Capture("great talk", "https://youtu.be/abc123?t=42", ["video"]),
            CaptureParser.Parse("great talk https://youtu.be/abc123?t=42 #video")
        );

    [Fact]
    public void Trailing_punctuation_and_url_fragments()
    {
        var c = CaptureParser.Parse("see https://example.com/page#section.");
        Assert.Equal("https://example.com/page#section", c!.Url);
        Assert.Empty(c.Tags);
    }

    [Fact]
    public void A_url_alone_on_the_first_line_becomes_the_link_with_a_markdown_body() =>
        Same(
            new Capture("## Why\n- fast\n- *fun*", "https://bun.sh", ["dev"]),
            CaptureParser.Parse("https://bun.sh\n\n## Why\n- fast\n- *fun*\n\n#dev")
        );

    [Fact]
    public void Markdown_structure_survives()
    {
        const string note =
            "# Plan\n\n- step one\n    - nested  step\n\n```c\n#include <stdio.h>\nint  x;\n```\n\nUse `#define` wisely #c";
        Same(
            new Capture("# Plan\n\n- step one\n    - nested  step\n\n```c\n#include <stdio.h>\nint  x;\n```\n\nUse `#define` wisely", null, ["c"]),
            CaptureParser.Parse(note)
        );
    }

    [Fact]
    public void Urls_in_markdown_links_code_or_longer_notes_stay_in_the_text()
    {
        Same(new Capture("see [the docs](https://bun.sh/docs)", null, ["dev"]), CaptureParser.Parse("see [the docs](https://bun.sh/docs) #dev"));
        Assert.Null(CaptureParser.Parse("run `curl https://x.dev/install`")!.Url);
        Assert.Null(CaptureParser.Parse("Links:\n- https://a.dev\n- https://b.dev")!.Url);
        Assert.Null(CaptureParser.Parse("compare https://a.dev and https://b.dev")!.Url);
    }

    [Fact]
    public void Editing_round_trips()
    {
        var item = Item.New("## Notes\n- one\n  - two", "https://bun.sh", ["dev", "reading"]);
        Same(new Capture(item.Text, item.Url, item.Tags), CaptureParser.Parse(CaptureParser.ToText(item)));
        var note = Item.New("plain *note*");
        Same(new Capture(note.Text, null, []), CaptureParser.Parse(CaptureParser.ToText(note)));
    }

    [Fact]
    public void Empty_and_tag_only_input_give_nothing_to_save()
    {
        Assert.Null(CaptureParser.Parse("   "));
        Assert.Null(CaptureParser.Parse("#just-a-tag"));
    }
}
