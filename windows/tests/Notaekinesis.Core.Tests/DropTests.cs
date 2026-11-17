using Notaekinesis.Core;

namespace Notaekinesis.Core.Tests;

public sealed class DropTests
{
    [Fact]
    public void A_dragged_hyperlink_keeps_its_text_as_the_title()
    {
        var c = DropReader.Read(
            "https://bun.sh/docs",
            "<html><body><!--StartFragment--><a href=\"https://bun.sh/docs\">Bun <b>Docs</b> &amp; guides</a><!--EndFragment--></body></html>",
            "Bun Docs & guides"
        );
        Assert.Equal("https://bun.sh/docs", c!.Url);
        Assert.Equal("Bun Docs & guides", c.Title);
        Assert.Equal("", c.Text);
    }

    [Fact]
    public void Plain_text_that_is_the_title_is_used_when_there_is_no_html()
    {
        Assert.Equal("Example Domain", DropReader.Read("https://example.com/", null, "Example Domain")!.Title);
        // The address bar often sends the URL as the text: that's no title.
        Assert.Null(DropReader.Read("https://example.com/", null, "https://example.com/")!.Title);
        // An anchor whose text is the URL isn't a title either.
        Assert.Null(DropReader.Read("https://x.dev", "<a href='https://x.dev/'>https://x.dev</a>", null)!.Title);
    }

    [Fact]
    public void Text_is_saved_as_it_is_and_a_lone_url_becomes_a_link()
    {
        var note = DropReader.Read(null, null, "  #1 rule: no tags from dropped text\nsecond line ");
        Assert.Equal("#1 rule: no tags from dropped text\nsecond line", note!.Text);
        Assert.Null(note.Url);
        Assert.Empty(note.Tags);

        var link = DropReader.Read(null, null, "https://example.com/page?q=1");
        Assert.Equal("https://example.com/page?q=1", link!.Url);
        Assert.Equal("", link.Text);
    }

    [Fact]
    public void Nothing_useful_gives_null_and_non_web_links_fall_back_to_text()
    {
        Assert.Null(DropReader.Read(null, null, "   "));
        Assert.Null(DropReader.Read(null, null, null));
        Assert.Equal("file:///C:/x.txt", DropReader.Read("file:///C:/x.txt", null, "file:///C:/x.txt")!.Text);
    }

    [Fact]
    public void Supported_files_by_extension()
    {
        Assert.True(DropReader.IsSupportedFile(@"C:\shots\Screenshot 2026.PNG"));
        Assert.True(DropReader.IsSupportedFile("clip.mov"));
        Assert.False(DropReader.IsSupportedFile("drawing.svg"));
        Assert.False(DropReader.IsSupportedFile("notes.txt"));
    }
}
