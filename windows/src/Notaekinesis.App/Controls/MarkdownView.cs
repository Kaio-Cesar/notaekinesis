using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Notaekinesis.Core.Markdown;
using MdBlock = Notaekinesis.Core.Markdown.Block;
using MdInline = Notaekinesis.Core.Markdown.Inline;
using MdLineBreak = Notaekinesis.Core.Markdown.LineBreak;
using MdParagraph = Notaekinesis.Core.Markdown.Paragraph;
using XamlInline = Microsoft.UI.Xaml.Documents.Inline;
using XamlParagraph = Microsoft.UI.Xaml.Documents.Paragraph;

namespace Notaekinesis.App.Controls;

/// <summary>Shows a note's Markdown as native text (a RichTextBlock), selectable, with clickable links.</summary>
public sealed partial class MarkdownView : ContentControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render())
    );

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines),
        typeof(int),
        typeof(MarkdownView),
        new PropertyMetadata(0, (d, _) => ((MarkdownView)d).Render())
    );

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    public MarkdownView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsTabStop = false;
        ActualThemeChanged += (_, _) => Render();
    }

    // Fluent's secondary and tertiary text colors. Theme resources can't be read from code
    // under NativeAOT (they come back as untyped objects), so the values are spelled out here.
    private SolidColorBrush _secondary = new();
    private SolidColorBrush _tertiary = new();

    private void Render()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        _secondary = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0xC5, 255, 255, 255) : Windows.UI.Color.FromArgb(0x9E, 0, 0, 0));
        _tertiary = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x87, 255, 255, 255) : Windows.UI.Color.FromArgb(0x72, 0, 0, 0));
        var rich = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = MaxLines,
        };
        foreach (var block in MarkdownParser.Parse(Markdown ?? "")) AddBlock(rich.Blocks, block, quoteDepth: 0);
        Content = rich;
    }

    private void AddBlock(BlockCollection blocks, MdBlock block, int quoteDepth)
    {
        var indent = quoteDepth * 14.0;
        var p = new XamlParagraph { Margin = new Thickness(indent, 0, 0, 6) };
        switch (block)
        {
            case MdParagraph para:
                AddInlines(p.Inlines, para.Inlines);
                break;
            case Heading h:
                p.FontWeight = FontWeights.SemiBold;
                p.FontSize = h.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 14 };
                p.Margin = new Thickness(indent, 4, 0, 4);
                AddInlines(p.Inlines, h.Inlines);
                break;
            case ListItem li:
                var left = indent + 4 + li.Depth * 8;
                p.Margin = new Thickness(left + 16, 0, 0, 2);
                p.TextIndent = -16; // hanging marker
                p.Inlines.Add(new Run { Text = li.Marker.Length > 1 && li.Marker.EndsWith('.') ? li.Marker + " " : li.Marker + "  " });
                AddInlines(p.Inlines, li.Inlines);
                break;
            case CodeBlock code:
                p.FontFamily = Mono;
                p.FontSize = 12.5;
                p.Margin = new Thickness(indent + 8, 2, 0, 8);
                p.Foreground = _secondary;
                p.Inlines.Add(new Run { Text = code.Text });
                break;
            case Quote q:
                foreach (var inner in q.Blocks) AddBlock(blocks, inner, quoteDepth + 1);
                return;
            case Rule:
                p.Foreground = _tertiary;
                p.Inlines.Add(new Run { Text = "———" });
                break;
        }
        if (quoteDepth > 0) p.Foreground = _secondary;
        blocks.Add(p);
    }

    private static void AddInlines(InlineCollection target, IEnumerable<MdInline> inlines)
    {
        foreach (var inline in inlines)
        {
            XamlInline? x = inline switch
            {
                Text t => new Run { Text = t.Value },
                Code c => new Run { Text = c.Value, FontFamily = Mono },
                MdLineBreak => new Microsoft.UI.Xaml.Documents.LineBreak(),
                Strong s => Span(new Bold(), s.Children),
                Emphasis e => Span(new Italic(), e.Children),
                Strike s => Span(new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough }, s.Children),
                Link l => Span(new Hyperlink { NavigateUri = new Uri(l.Url) }, l.Children),
                _ => null,
            };
            if (x is not null) target.Add(x);
        }
    }

    private static Span Span(Span span, IEnumerable<MdInline> children)
    {
        AddInlines(span.Inlines, children);
        return span;
    }
}
