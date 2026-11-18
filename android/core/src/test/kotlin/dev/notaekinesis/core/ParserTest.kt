package dev.notaekinesis.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** Mirrors tests/capture.test.ts (and the Windows client's tests) so every client reads text the same way. */
class CaptureTest {
    private fun same(expected: Capture, actual: Capture?) {
        assertEquals(expected.text, actual!!.text)
        assertEquals(expected.url, actual.url)
        assertEquals(expected.tags, actual.tags)
    }

    @Test
    fun a_bare_url_becomes_a_link_with_no_text() =
        same(Capture("", "https://bun.sh/docs", emptyList()), CaptureParser.parse("  https://bun.sh/docs  "))

    @Test
    fun plain_text_stays_a_note() = same(Capture("buy milk\nand eggs", null, emptyList()), CaptureParser.parse("buy milk\r\nand eggs"))

    @Test
    fun tags_are_pulled_out_and_lower_cased() =
        same(Capture("Read this later", null, listOf("reading", "dev")), CaptureParser.parse("Read this later #Reading #dev #reading"))

    @Test
    fun a_url_with_a_comment_becomes_a_link_with_text() = same(
        Capture("great talk", "https://youtu.be/abc123?t=42", listOf("video")),
        CaptureParser.parse("great talk https://youtu.be/abc123?t=42 #video"),
    )

    @Test
    fun trailing_punctuation_and_url_fragments() {
        val c = CaptureParser.parse("see https://example.com/page#section.")!!
        assertEquals("https://example.com/page#section", c.url)
        assertEquals(emptyList(), c.tags)
    }

    @Test
    fun a_url_alone_on_the_first_line_becomes_the_link_with_a_markdown_body() = same(
        Capture("## Why\n- fast\n- *fun*", "https://bun.sh", listOf("dev")),
        CaptureParser.parse("https://bun.sh\n\n## Why\n- fast\n- *fun*\n\n#dev"),
    )

    @Test
    fun markdown_structure_survives() = same(
        Capture("# Plan\n\n- step one\n    - nested  step\n\n```c\n#include <stdio.h>\nint  x;\n```\n\nUse `#define` wisely", null, listOf("c")),
        CaptureParser.parse("# Plan\n\n- step one\n    - nested  step\n\n```c\n#include <stdio.h>\nint  x;\n```\n\nUse `#define` wisely #c"),
    )

    @Test
    fun urls_in_markdown_links_code_or_longer_notes_stay_in_the_text() {
        same(Capture("see [the docs](https://bun.sh/docs)", null, listOf("dev")), CaptureParser.parse("see [the docs](https://bun.sh/docs) #dev"))
        assertNull(CaptureParser.parse("run `curl https://x.dev/install`")!!.url)
        assertNull(CaptureParser.parse("Links:\n- https://a.dev\n- https://b.dev")!!.url)
        assertNull(CaptureParser.parse("compare https://a.dev and https://b.dev")!!.url)
    }

    @Test
    fun editing_round_trips() {
        val item = Item.new("## Notes\n- one\n  - two", "https://bun.sh", listOf("dev", "reading"))
        same(Capture(item.text, item.url, item.tags), CaptureParser.parse(CaptureParser.toText(item)))
        val note = Item.new("plain *note*")
        same(Capture(note.text, null, emptyList()), CaptureParser.parse(CaptureParser.toText(note)))
    }

    @Test
    fun empty_and_tag_only_input_give_nothing_to_save() {
        assertNull(CaptureParser.parse("   "))
        assertNull(CaptureParser.parse("#just-a-tag"))
    }
}

class MarkdownTest {
    /** A compact form that's easy to compare: text as-is, **b**, *i*, ~~s~~, `c`, [text](url), / for breaks. */
    private fun show(inlines: List<Inline>): String = inlines.joinToString("") {
        when (it) {
            is Inline.Text -> it.value
            is Inline.Code -> "`${it.value}`"
            Inline.LineBreak -> "/"
            is Inline.Strong -> "**${show(it.children)}**"
            is Inline.Emphasis -> "*${show(it.children)}*"
            is Inline.Strike -> "~~${show(it.children)}~~"
            is Inline.Link -> "[${show(it.children)}](${it.url})"
        }
    }

    private fun show(block: Block): String = when (block) {
        is Block.Paragraph -> "p:" + show(block.inlines)
        is Block.Heading -> "h${block.level}:" + show(block.inlines)
        is Block.ListItem -> "li${block.depth}${block.marker}:" + show(block.inlines)
        is Block.CodeBlock -> "code:" + block.text
        is Block.Quote -> "quote:" + block.blocks.joinToString("|") { show(it) }
        Block.Rule -> "hr"
    }

    private fun inl(s: String) = show(MarkdownParser.parseInlines(s))

    @Test
    fun emphasis_code_and_links() {
        assertEquals("a **bold** and *it* `x*y*`", inl("a __bold__ and _it_ `x*y*`"))
        assertEquals("**a *b* c**", inl("**a *b* c**"))
        assertEquals("[the docs](https://bun.sh/docs)", inl("[the docs](https://bun.sh/docs)"))
        assertEquals("~~old~~", inl("~~old~~"))
        assertEquals("[https://x.dev](https://x.dev)", inl("<https://x.dev>"))
    }

    @Test
    fun bare_urls_become_links_without_trailing_punctuation() =
        assertEquals("see [https://example.com/a_b](https://example.com/a_b), then", inl("see https://example.com/a_b, then"))

    @Test
    fun unsafe_links_html_and_snake_case_stay_text() {
        assertEquals("[x](javascript:alert(1))", inl("[x](javascript:alert(1))"))
        assertEquals("snake_case_name and a * b * c", inl("snake_case_name and a * b * c"))
        assertEquals("<b>not html</b>", inl("<b>not html</b>"))
        assertEquals("*literal*", inl("""\*literal\*"""))
    }

    @Test
    fun single_newlines_are_line_breaks() = assertEquals(listOf("p:one/two"), MarkdownParser.parse("one\ntwo").map(::show))

    @Test
    fun blocks() = assertEquals(
        listOf("h1:Plan", "li0•:step one", "li2•:nested", "li01.:first", "li0☐:todo", "li0☑:done",
            "code:#include <stdio.h>\n\nint x;", "quote:p:quoted *text*", "hr", "p:end"),
        MarkdownParser.parse("# Plan\n\n- step one\n    - nested\n1. first\n- [ ] todo\n- [x] done\n\n```c\n#include <stdio.h>\n\nint x;\n```\n> quoted *text*\n\n---\nend")
            .map(::show),
    )

    @Test
    fun an_unclosed_fence_runs_to_the_end() = assertEquals(listOf("code:code\nmore"), MarkdownParser.parse("```\ncode\nmore").map(::show))
}
