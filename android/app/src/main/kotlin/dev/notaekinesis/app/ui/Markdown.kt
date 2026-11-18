package dev.notaekinesis.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import dev.notaekinesis.core.Block
import dev.notaekinesis.core.Inline
import dev.notaekinesis.core.MarkdownParser

/** Renders a note's Markdown with Material type styles. Links open through the platform's URI handler. */
@Composable
fun MarkdownText(markdown: String, modifier: Modifier = Modifier) {
    val blocks = remember(markdown) { MarkdownParser.parse(markdown) }
    Column(modifier, verticalArrangement = Arrangement.spacedBy(6.dp)) {
        blocks.forEach { MarkdownBlock(it) }
    }
}

@Composable
private fun MarkdownBlock(block: Block) {
    val type = MaterialTheme.typography
    val colors = MaterialTheme.colorScheme
    when (block) {
        is Block.Paragraph -> Text(inlines(block.inlines), style = type.bodyMedium)
        is Block.Heading -> Text(
            inlines(block.inlines),
            style = when (block.level) {
                1 -> type.titleLarge
                2 -> type.titleMedium
                else -> type.titleSmall
            },
        )
        is Block.ListItem -> Row(Modifier.padding(start = (block.depth * 16).dp)) {
            Text(block.marker, style = type.bodyMedium, color = colors.onSurfaceVariant, modifier = Modifier.width(22.dp))
            Text(inlines(block.inlines), style = type.bodyMedium)
        }
        is Block.CodeBlock -> Box(
            Modifier.fillMaxWidth()
                .background(colors.surfaceContainerHighest, RoundedCornerShape(8.dp))
                .horizontalScroll(rememberScrollState())
                .padding(10.dp),
        ) {
            Text(block.text, style = type.bodySmall.copy(fontFamily = FontFamily.Monospace))
        }
        is Block.Quote -> Row(Modifier.height(IntrinsicSize.Min)) {
            Box(Modifier.width(3.dp).fillMaxHeight().background(colors.outlineVariant, RoundedCornerShape(2.dp)))
            Column(Modifier.padding(start = 10.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                block.blocks.forEach { MarkdownBlock(it) }
            }
        }
        Block.Rule -> HorizontalDivider(Modifier.padding(vertical = 4.dp))
    }
}

@Composable
private fun inlines(list: List<Inline>): AnnotatedString {
    val colors = MaterialTheme.colorScheme
    val code = SpanStyle(fontFamily = FontFamily.Monospace, background = colors.surfaceContainerHighest)
    val link = TextLinkStyles(SpanStyle(color = colors.primary, textDecoration = TextDecoration.Underline))
    return remember(list, colors) {
        buildAnnotatedString {
            fun append(items: List<Inline>) {
                for (inline in items) when (inline) {
                    is Inline.Text -> append(inline.value)
                    is Inline.Code -> withStyle(code) { append(inline.value) }
                    Inline.LineBreak -> append('\n')
                    is Inline.Strong -> withStyle(SpanStyle(fontWeight = FontWeight.SemiBold)) { append(inline.children) }
                    is Inline.Emphasis -> withStyle(SpanStyle(fontStyle = FontStyle.Italic)) { append(inline.children) }
                    is Inline.Strike -> withStyle(SpanStyle(textDecoration = TextDecoration.LineThrough)) { append(inline.children) }
                    is Inline.Link -> withLink(LinkAnnotation.Url(inline.url, link)) { append(inline.children) }
                }
            }
            append(list)
        }
    }
}

/** For one-line previews (the share sheet): the text without Markdown syntax. */
fun plainText(markdown: String): String = MarkdownParser.parse(markdown).joinToString(" ") { block ->
    fun flat(list: List<Inline>): String = list.joinToString("") {
        when (it) {
            is Inline.Text -> it.value
            is Inline.Code -> it.value
            Inline.LineBreak -> " "
            is Inline.Strong -> flat(it.children)
            is Inline.Emphasis -> flat(it.children)
            is Inline.Strike -> flat(it.children)
            is Inline.Link -> flat(it.children)
        }
    }
    when (block) {
        is Block.Paragraph -> flat(block.inlines)
        is Block.Heading -> flat(block.inlines)
        is Block.ListItem -> flat(block.inlines)
        is Block.CodeBlock -> block.text
        is Block.Quote -> block.blocks.joinToString(" ") { if (it is Block.Paragraph) flat(it.inlines) else "" }
        Block.Rule -> ""
    }
}
