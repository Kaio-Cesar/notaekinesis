package dev.notaekinesis.app.ui

import android.text.format.DateUtils
import android.text.format.Formatter
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material.icons.outlined.Movie
import androidx.compose.material.icons.outlined.PictureAsPdf
import androidx.compose.material.icons.outlined.PushPin
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import dev.notaekinesis.app.session
import dev.notaekinesis.core.Item
import dev.notaekinesis.core.ItemKind
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/** One note, link or file in the feed. Tap opens it (or edits a note); long-press shows actions. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun ItemCard(item: Item, onOpen: () -> Unit, onLongPress: () -> Unit, onTag: (String) -> Unit, modifier: Modifier = Modifier) {
    val type = MaterialTheme.typography
    val colors = MaterialTheme.colorScheme
    ElevatedCard(modifier.fillMaxWidth()) {
        Column(
            Modifier.combinedClickable(onClick = onOpen, onLongClick = onLongPress).padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            when (item.kind) {
                ItemKind.Link -> LinkHeader(item)
                ItemKind.File -> FileBody(item)
                ItemKind.Note -> Unit
            }
            if (item.text.isNotBlank()) MarkdownText(item.text)

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (item.pinned) Icon(Icons.Outlined.PushPin, "Pinned", Modifier.size(14.dp), tint = colors.primary)
                FlowRow(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    item.tags.forEach { tag ->
                        Text(
                            "#$tag",
                            style = type.labelMedium,
                            color = colors.primary,
                            modifier = Modifier.clip(RoundedCornerShape(4.dp)).combinedClickable(onClick = { onTag(tag) }),
                        )
                    }
                }
                Text(ago(item.createdAt), style = type.labelSmall, color = colors.onSurfaceVariant)
            }
        }
    }
}

@Composable
private fun LinkHeader(item: Item) {
    val url = item.url ?: return
    val host = url.toHttpUrlOrNull()?.host?.removePrefix("www.") ?: url
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        if (item.faviconUrl != null) {
            AsyncImage(item.faviconUrl, null, Modifier.size(20.dp).clip(RoundedCornerShape(4.dp)))
        }
        Column(Modifier.weight(1f)) {
            Text(
                item.title?.takeIf { it.isNotBlank() } ?: url,
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.SemiBold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Text(item.siteName ?: host, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
        }
    }
    item.description?.takeIf { it.isNotBlank() }?.let {
        Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 3, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun FileBody(item: Item) {
    val file = item.file ?: return
    val context = LocalContext.current
    val api = context.session.state.value.api
    if (file.isImage && api != null) {
        AsyncImage(
            model = api.fileUrl(file.id).toString(),
            contentDescription = file.name,
            contentScale = ContentScale.FillWidth,
            modifier = Modifier.fillMaxWidth().heightIn(max = 360.dp).clip(RoundedCornerShape(8.dp)),
        )
        return
    }
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        val icon = when {
            file.isImage -> Icons.Outlined.Image
            file.isVideo -> Icons.Outlined.Movie
            file.mime == "application/pdf" -> Icons.Outlined.PictureAsPdf
            else -> Icons.Outlined.Description
        }
        Icon(icon, null, tint = MaterialTheme.colorScheme.primary)
        Column(Modifier.weight(1f)) {
            Text(file.name, style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Text(Formatter.formatShortFileSize(context, file.size), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

private fun ago(time: Long): String {
    val now = System.currentTimeMillis()
    if (now - time < DateUtils.MINUTE_IN_MILLIS) return "now"
    return DateUtils.getRelativeTimeSpanString(time, now, DateUtils.MINUTE_IN_MILLIS, DateUtils.FORMAT_ABBREV_ALL).toString()
}
