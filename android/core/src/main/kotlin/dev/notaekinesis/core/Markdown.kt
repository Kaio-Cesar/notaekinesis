package dev.notaekinesis.core

// A small Markdown reader for note cards: the common subset the web app renders (headings,
// paragraphs, lists, quotes, code, rules, emphasis, links). Raw HTML is shown as text, like the
// web app (markdown-it with html: false). Single newlines are line breaks (breaks: true).
// A port of the Windows client's Markdown.cs; keep the two in step.

sealed interface Inline {
    data class Text(val value: String) : Inline
    data class Code(val value: String) : Inline
    data object LineBreak : Inline
    data class Strong(val children: List<Inline>) : Inline
    data class Emphasis(val children: List<Inline>) : Inline
    data class Strike(val children: List<Inline>) : Inline

    /** Only http(s) and mailto links; anything else is kept as text. */
    data class Link(val children: List<Inline>, val url: String) : Inline
}

sealed interface Block {
    data class Paragraph(val inlines: List<Inline>) : Block
    data class Heading(val level: Int, val inlines: List<Inline>) : Block
    data class CodeBlock(val text: String) : Block
    data class Quote(val blocks: List<Block>) : Block
    data object Rule : Block

    /** [depth] is 0 for top-level items; [marker] is "•", "1.", or "☐"/"☑" for task items. */
    data class ListItem(val depth: Int, val marker: String, val inlines: List<Inline>) : Block
}

object MarkdownParser {
    private val headingRe = Regex("""^(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$""")
    private val listRe = Regex("""^([ \t]*)([-*+]|(\d{1,9})[.)])[ \t]+(?:\[([ xX])\][ \t]+)?(.*)$""")
    private val fenceRe = Regex("""^[ \t]{0,3}(```|~~~)""")
    private val ruleRe = Regex("""^[ \t]{0,3}([-*_])([ \t]*\1){2,}[ \t]*$""")
    private val quoteRe = Regex("""^[ \t]{0,3}>[ \t]?""")
    private val linkRe = Regex("""^\[([^\]\n]*)\]\(([^()\s]+)(?:[ \t]+"[^"]*")?\)""")
    private val bareUrlRe = Regex("""^https?://[^\s<>"'`]+[^\s<>"'`.,;:!?)\]]""", RegexOption.IGNORE_CASE)
    private val autolinkRe = Regex("""^<((?:https?://|mailto:)[^>\s]+)>""", RegexOption.IGNORE_CASE)

    fun parse(markdown: String): List<Block> = parseBlocks(CaptureParser.normalizeLineEndings(markdown).split('\n'))

    private fun parseBlocks(lines: List<String>): List<Block> {
        val blocks = mutableListOf<Block>()
        val para = mutableListOf<String>()
        fun flush() {
            if (para.isEmpty()) return
            blocks += Block.Paragraph(parseInlines(para.joinToString("\n")))
            para.clear()
        }

        var i = 0
        while (i < lines.size) {
            val line = lines[i]
            if (line.isBlank()) {
                flush()
                i++
                continue
            }

            val fence = fenceRe.find(line)
            if (fence != null) {
                flush()
                val code = mutableListOf<String>()
                i++
                while (i < lines.size && !lines[i].trimStart().startsWith(fence.groupValues[1])) code += lines[i++]
                blocks += Block.CodeBlock(code.joinToString("\n"))
                i++
                continue
            }

            if (ruleRe.matches(line) && para.isEmpty()) {
                blocks += Block.Rule
                i++
                continue
            }

            val heading = headingRe.find(line)
            if (heading != null) {
                flush()
                blocks += Block.Heading(heading.groupValues[1].length, parseInlines(heading.groupValues[2]))
                i++
                continue
            }

            if (quoteRe.containsMatchIn(line)) {
                flush()
                val quoted = mutableListOf<String>()
                while (i < lines.size && quoteRe.containsMatchIn(lines[i])) quoted += quoteRe.replaceFirst(lines[i++], "")
                blocks += Block.Quote(parseBlocks(quoted))
                continue
            }

            val item = listRe.find(line)
            if (item != null) {
                flush()
                val indent = item.groupValues[1].replace("\t", "    ").length
                val marker = when {
                    item.groups[4] != null -> if (item.groupValues[4] == " ") "☐" else "☑"
                    item.groups[3] != null -> item.groupValues[3] + "."
                    else -> "•"
                }
                val text = StringBuilder(item.groupValues[5])
                // Lazy continuation: following plain lines belong to the item.
                while (i + 1 < lines.size && lines[i + 1].isNotBlank() && !listRe.containsMatchIn(lines[i + 1]) &&
                    !fenceRe.containsMatchIn(lines[i + 1]) && !headingRe.containsMatchIn(lines[i + 1]) &&
                    !quoteRe.containsMatchIn(lines[i + 1])
                ) {
                    text.append('\n').append(lines[++i].trim())
                }
                blocks += Block.ListItem(minOf(indent / 2, 6), marker, parseInlines(text.toString()))
                i++
                continue
            }

            para += line.trim()
            i++
        }
        flush()
        return blocks
    }

    fun parseInlines(s: String): List<Inline> {
        val result = mutableListOf<Inline>()
        val text = StringBuilder()
        fun flushText() {
            if (text.isEmpty()) return
            result += Inline.Text(text.toString())
            text.clear()
        }
        fun add(inline: Inline) {
            flushText()
            result += inline
        }

        var i = 0
        while (i < s.length) {
            val c = s[i]

            if (c == '\\' && i + 1 < s.length && !isAsciiLetterOrDigit(s[i + 1]) && s[i + 1] != '\n') {
                text.append(s[i + 1])
                i += 2
                continue
            }
            if (c == '\n') {
                add(Inline.LineBreak)
                i++
                continue
            }
            if (c == '`') {
                val ticks = countRun(s, i, '`')
                val close = s.indexOf("`".repeat(ticks), i + ticks)
                if (close > 0) {
                    add(Inline.Code(s.substring(i + ticks, close).trim()))
                    i = close + ticks
                    continue
                }
            }
            if (c == '[') {
                val link = linkRe.find(s.substring(i))
                if (link != null && isSafeUrl(link.groupValues[2])) {
                    add(Inline.Link(parseInlines(link.groupValues[1]), link.groupValues[2]))
                    i += link.value.length
                    continue
                }
            }
            if (c == '<') {
                val auto = autolinkRe.find(s.substring(i))
                if (auto != null) {
                    add(Inline.Link(listOf(Inline.Text(auto.groupValues[1])), auto.groupValues[1]))
                    i += auto.value.length
                    continue
                }
            }
            if ((c == 'h' || c == 'H') && (i == 0 || !s[i - 1].isLetterOrDigit())) {
                val bare = bareUrlRe.find(s.substring(i))
                if (bare != null) {
                    add(Inline.Link(listOf(Inline.Text(bare.value)), bare.value))
                    i += bare.value.length
                    continue
                }
            }
            if (s.startsWith("**", i) || s.startsWith("__", i) || s.startsWith("~~", i)) {
                val found = delimited(s, i, 2)
                if (found != null) {
                    val children = parseInlines(found.first)
                    add(if (c == '~') Inline.Strike(children) else Inline.Strong(children))
                    i = found.second
                    continue
                }
            }
            if (c == '*' || c == '_') {
                val found = delimited(s, i, 1)
                if (found != null) {
                    add(Inline.Emphasis(parseInlines(found.first)))
                    i = found.second
                    continue
                }
            }

            text.append(c)
            i++
        }
        flushText()
        return result
    }

    private fun isAsciiLetterOrDigit(c: Char) = c in 'a'..'z' || c in 'A'..'Z' || c in '0'..'9'

    private fun countRun(s: String, at: Int, c: Char): Int {
        var n = 0
        while (at + n < s.length && s[at + n] == c) n++
        return n
    }

    /** Finds the closing delimiter for emphasis at [at], following CommonMark's flanking rules loosely. Returns (inner, end). */
    private fun delimited(s: String, at: Int, width: Int): Pair<String, Int>? {
        val delim = s.substring(at, at + width)
        val start = at + width
        // Opening: not followed by space; `_` must not sit inside a word (snake_case).
        if (start >= s.length || s[start].isWhitespace()) return null
        if (delim[0] == '_' && at > 0 && s[at - 1].isLetterOrDigit()) return null
        if (width == 1 && s[start] == delim[0]) return null

        var j = start + 1
        while (j <= s.length - width) {
            if (s[j] == '`') {
                // Don't close inside a code span.
                val close = s.indexOf('`', j + 1)
                if (close > 0) j = close
                j++
                continue
            }
            val ok = s.startsWith(delim, j) && !s[j - 1].isWhitespace() &&
                !(width == 1 && ((j + 1 < s.length && s[j + 1] == delim[0]) || s[j - 1] == delim[0])) &&
                !(delim[0] == '_' && j + width < s.length && s[j + width].isLetterOrDigit())
            if (ok) return s.substring(start, j) to j + width
            j++
        }
        return null
    }

    fun isSafeUrl(url: String) =
        url.startsWith("http://", ignoreCase = true) || url.startsWith("https://", ignoreCase = true) ||
            url.startsWith("mailto:", ignoreCase = true)
}
