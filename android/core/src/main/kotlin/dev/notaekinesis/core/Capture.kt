package dev.notaekinesis.core

/** What the quick-add box and shared text turn into an item. Text is Markdown. */
data class Capture(val text: String, val url: String?, val tags: List<String>, val title: String? = null)

/** A port of web/src/lib/capture.ts; keep the two in step (the tests mirror tests/capture.test.ts). */
object CaptureParser {
    private val urlRe = Regex("""\bhttps?://[^\s<>"'`]+[^\s<>"'`.,;:!?)\]]""", RegexOption.IGNORE_CASE)

    // `#tag` at the start of a line or after a space, followed by a space or the end of the line.
    // `# Heading` (with a space) is a Markdown heading and doesn't match.
    private val tagRe = Regex("""(?<=^|[ \t])#([\p{L}\p{N}_-]{1,64})(?=[ \t]|$)""", RegexOption.MULTILINE)

    // Parts of Markdown that must be left untouched: fenced code, inline code, links and autolinks.
    private val protectedRe =
        Regex("""(^|\n)(```|~~~)[\s\S]*?(\n\2[^\n]*(?=\n|\z)|\z)|`[^`\n]+`|!?\[[^\]\n]*\]\([^)\n]*\)|<https?:[^>\s]+>""")

    private val gaps = Regex("\n{3,}")
    // CRLF, CR, NEL, line and paragraph separators, form feed: the same set as .NET's ReplaceLineEndings.
    private val lineEndings = Regex("""\r\n|[\r\x{85}\x{2028}\x{2029}\f]""")

    private fun protectedRanges(text: String) = protectedRe.findAll(text).map { it.range.first to it.range.last + 1 }.toList()

    private fun inside(ranges: List<Pair<Int, Int>>, start: Int, end: Int) = ranges.any { start < it.second && end > it.first }

    /** Removes text[start, end) without disturbing the rest of the Markdown. */
    private fun cut(text: String, start: Int, end: Int): String {
        var before = text.substring(0, start)
        var after = text.substring(end)
        val atLineStart = before.isEmpty() || before.endsWith('\n')
        if (atLineStart) after = after.trimStart(' ', '\t')
        else if (after.isEmpty() || after[0] in " \t\n") before = before.trimEnd(' ', '\t')
        return before + after
    }

    fun normalizeLineEndings(s: String) = lineEndings.replace(s, "\n")

    /**
     * Parses free text the way a chat box would, keeping it valid Markdown: `#tags` become tags; a
     * bare URL becomes the link when it's a one-liner or sits alone on the first line; everything
     * else stays in the note. Null if there's nothing to save.
     */
    fun parse(input: String): Capture? {
        var text = normalizeLineEndings(input).trim()
        if (text.isEmpty()) return null

        val tags = ArrayDeque<String>()
        var ranges = protectedRanges(text)
        for (m in tagRe.findAll(text).toList().asReversed()) {
            if (inside(ranges, m.range.first, m.range.last + 1)) continue
            tags.addFirst(m.groupValues[1].lowercase())
            text = cut(text, m.range.first, m.range.last + 1)
        }
        text = gaps.replace(text, "\n\n").trim()

        var url: String? = null
        ranges = protectedRanges(text)
        val bare = urlRe.findAll(text).filter { !inside(ranges, it.range.first, it.range.last + 1) }.toList()
        if (bare.isNotEmpty()) {
            val first = bare[0]
            val firstLine = text.substringBefore('\n')
            val oneLiner = '\n' !in text && bare.size == 1
            val aloneOnFirstLine = first.range.first == 0 && firstLine.trim() == first.value
            if (oneLiner || aloneOnFirstLine) {
                url = first.value
                text = cut(text, first.range.first, first.range.last + 1).trim()
            }
        }

        if (text.isEmpty() && url == null) return null
        return Capture(text, url, tags.distinct())
    }

    /** Turns an item back into the text [parse] reads, for editing. */
    fun toText(item: Item): String {
        val tags = item.tags.joinToString(" ") { "#$it" }
        return listOf(item.url, item.text, tags).filter { !it.isNullOrEmpty() }.joinToString("\n\n")
    }
}
