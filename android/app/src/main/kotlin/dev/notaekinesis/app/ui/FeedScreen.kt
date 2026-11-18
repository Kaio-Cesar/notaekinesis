package dev.notaekinesis.app.ui

import android.content.ClipData
import android.content.Intent
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.outlined.AccountCircle
import androidx.compose.material.icons.outlined.Archive
import androidx.compose.material.icons.outlined.AttachFile
import androidx.compose.material.icons.outlined.CloudOff
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Edit
import androidx.compose.material.icons.outlined.PushPin
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.Share
import androidx.compose.material.icons.outlined.Unarchive
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Surface
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.rememberVectorPainter
import androidx.compose.ui.layout.ContentScale
import coil3.compose.AsyncImage
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.notaekinesis.app.SharedFile
import dev.notaekinesis.app.SupportedMimeTypes
import dev.notaekinesis.app.openAttachment
import dev.notaekinesis.app.openLink
import dev.notaekinesis.app.session
import dev.notaekinesis.core.CaptureParser
import dev.notaekinesis.core.Item
import dev.notaekinesis.core.ItemKind
import dev.notaekinesis.core.sync.SyncStatus
import kotlinx.coroutines.launch
import java.io.IOException

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FeedScreen(vm: FeedViewModel, onSignOut: () -> Unit) {
    val context = LocalContext.current
    val session = context.session
    val feed by vm.feed.collectAsStateWithLifecycle()
    val filter by vm.filter.collectAsState()
    val status by session.syncStatus.collectAsStateWithLifecycle(SyncStatus.Idle)
    val state by session.state.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val listState = rememberLazyListState()
    var actionsFor by remember { mutableStateOf<Item?>(null) }
    var editing by remember { mutableStateOf<Item?>(null) }

    fun showError(e: Throwable) = scope.launch { snackbar.showSnackbar(e.message ?: "Something went wrong.") }

    /** Applies a change with an Undo that puts the previous version back. */
    fun withUndo(message: String, previous: Item, change: () -> Unit) {
        change()
        scope.launch {
            if (snackbar.showSnackbar(message, "Undo", duration = SnackbarDuration.Short) == SnackbarResult.ActionPerformed) vm.restore(previous)
        }
    }

    fun open(item: Item) = when (item.kind) {
        ItemKind.Link -> context.openLink(item.url!!)
        ItemKind.File -> {
            scope.launch {
                try {
                    context.openAttachment(item.file!!)
                } catch (e: IOException) {
                    showError(e)
                }
            }
            Unit
        }
        ItemKind.Note -> editing = item
    }

    val pickFiles = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        scope.launch {
            snackbar.showSnackbar(if (uris.size == 1) "Uploading…" else "Uploading ${uris.size} files…", duration = SnackbarDuration.Short)
        }
        scope.launch {
            for (uri in uris) {
                try {
                    val file = SharedFile(context, uri)
                    session.saveFile(file.body(), file.name) { copy(tags = filter.tags) }
                } catch (e: IOException) {
                    showError(e)
                }
            }
        }
    }

    BackHandler(enabled = filter.archived) { vm.setArchived(false) }

    Scaffold(
        topBar = {
            TopAppBar(
                navigationIcon = {
                    if (filter.archived) IconButton({ vm.setArchived(false) }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") }
                },
                title = {
                    SearchField(filter.search, vm::setSearch, if (filter.archived) "Search the archive" else "Search")
                },
                actions = {
                    if (status == SyncStatus.Offline) {
                        Icon(Icons.Outlined.CloudOff, "Offline; changes will sync later", Modifier.padding(horizontal = 8.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    AccountMenu(
                        name = state.user?.name,
                        picture = state.user?.image,
                        server = state.serverUrl,
                        archived = filter.archived,
                        onArchive = { vm.setArchived(!filter.archived) },
                        onSync = { state.sync?.requestSync() },
                        onSignOut = onSignOut,
                    )
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = {
            if (!filter.archived) Composer(onSend = vm::add, onAttach = { pickFiles.launch(SupportedMimeTypes) })
        },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            if (feed.tags.isNotEmpty() || filter.tags.isNotEmpty()) {
                val selected = filter.tags
                val tags = selected + feed.tags.map { it.tag }.filter { it !in selected }
                LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(tags, key = { it }) { tag ->
                        FilterChip(selected = tag in selected, onClick = { vm.toggleTag(tag) }, label = { Text("#$tag") })
                    }
                }
            }
            if (feed.loaded && feed.items.isEmpty()) {
                Empty(filter)
            }
            LazyColumn(
                state = listState,
                contentPadding = PaddingValues(12.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                items(feed.items, key = { it.id }) { item ->
                    SwipeableItem(
                        item = item,
                        onArchive = {
                            withUndo(if (item.archived) "Moved out of the archive" else "Archived", item) {
                                vm.save(item.copy(archived = !item.archived))
                            }
                        },
                        onDelete = { withUndo("Deleted", item) { vm.delete(item) } },
                        modifier = Modifier.animateItem(),
                    ) {
                        ItemCard(item, onOpen = { open(item) }, onLongPress = { actionsFor = item }, onTag = vm::toggleTag)
                    }
                }
            }
        }
    }

    // Scroll to the top when something new arrives at the top (e.g. just added).
    val first = feed.items.firstOrNull()?.id
    LaunchedEffect(first) { if (first != null && listState.firstVisibleItemIndex <= 1) listState.animateScrollToItem(0) }

    actionsFor?.let { item ->
        ItemActions(
            item = item,
            onDismiss = { actionsFor = null },
            onPin = { vm.save(item.copy(pinned = !item.pinned)) },
            onEdit = { editing = item },
            onArchive = {
                withUndo(if (item.archived) "Moved out of the archive" else "Archived", item) { vm.save(item.copy(archived = !item.archived)) }
            },
            onDelete = { withUndo("Deleted", item) { vm.delete(item) } },
        )
    }
    editing?.let { item ->
        EditSheet(item, onDismiss = { editing = null }, onSave = { text -> vm.edit(item, text) })
    }
}

@Composable
private fun SearchField(value: String, onChange: (String) -> Unit, placeholder: String) {
    TextField(
        value = value,
        onValueChange = onChange,
        placeholder = { Text(placeholder) },
        leadingIcon = { Icon(Icons.Outlined.Search, null) },
        singleLine = true,
        shape = RoundedCornerShape(28.dp),
        colors = TextFieldDefaults.colors(
            focusedIndicatorColor = androidx.compose.ui.graphics.Color.Transparent,
            unfocusedIndicatorColor = androidx.compose.ui.graphics.Color.Transparent,
        ),
        modifier = Modifier.fillMaxWidth().padding(end = 4.dp),
    )
}

@Composable
private fun AccountMenu(
    name: String?,
    picture: String?,
    server: String?,
    archived: Boolean,
    onArchive: () -> Unit,
    onSync: () -> Unit,
    onSignOut: () -> Unit,
) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton({ open = true }) {
            // The picture from the identity provider; the generic icon while it loads or if there's none.
            val fallback = rememberVectorPainter(Icons.Outlined.AccountCircle)
            if (picture.isNullOrBlank()) {
                Icon(Icons.Outlined.AccountCircle, "Account")
            } else {
                AsyncImage(
                    model = remember(picture) { pictureModel(picture) },
                    contentDescription = "Account",
                    placeholder = fallback,
                    error = fallback,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.size(32.dp).clip(CircleShape),
                )
            }
        }
        DropdownMenu(open, { open = false }) {
            if (name != null || server != null) {
                Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                    name?.let { Text(it, style = MaterialTheme.typography.titleSmall) }
                    server?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
                HorizontalDivider()
            }
            DropdownMenuItem(
                text = { Text(if (archived) "Back to notes" else "Archive") },
                leadingIcon = { Icon(if (archived) Icons.Outlined.Unarchive else Icons.Outlined.Archive, null) },
                onClick = { open = false; onArchive() },
            )
            DropdownMenuItem(text = { Text("Sync now") }, onClick = { open = false; onSync() })
            DropdownMenuItem(text = { Text("Sign out") }, onClick = { open = false; onSignOut() })
        }
    }
}

/** Swipe right to archive, left to delete. The row snaps back; the change comes with an Undo. */
@Composable
private fun SwipeableItem(
    item: Item,
    onArchive: () -> Unit,
    onDelete: () -> Unit,
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit,
) {
    val swipe = rememberSwipeToDismissBoxState()
    val scope = rememberCoroutineScope()
    SwipeToDismissBox(
        state = swipe,
        modifier = modifier,
        onDismiss = { value ->
            when (value) {
                SwipeToDismissBoxValue.StartToEnd -> onArchive()
                SwipeToDismissBoxValue.EndToStart -> onDelete()
                SwipeToDismissBoxValue.Settled -> Unit
            }
            scope.launch { swipe.reset() }
        },
        backgroundContent = {
            val toArchive = swipe.dismissDirection == SwipeToDismissBoxValue.StartToEnd
            val colors = MaterialTheme.colorScheme
            Box(
                Modifier.fillMaxSize().background(
                    if (toArchive) colors.secondaryContainer else colors.errorContainer,
                    RoundedCornerShape(12.dp),
                ).padding(horizontal = 20.dp),
                contentAlignment = if (toArchive) Alignment.CenterStart else Alignment.CenterEnd,
            ) {
                Icon(
                    when {
                        !toArchive -> Icons.Outlined.Delete
                        item.archived -> Icons.Outlined.Unarchive
                        else -> Icons.Outlined.Archive
                    },
                    null,
                    tint = if (toArchive) colors.onSecondaryContainer else colors.onErrorContainer,
                )
            }
        },
    ) { content() }
}

@Composable
private fun Composer(onSend: (String) -> Boolean, onAttach: () -> Unit) {
    var text by rememberSaveable { mutableStateOf("") }
    Surface(tonalElevation = 3.dp) {
        Row(
            Modifier.fillMaxWidth().navigationBarsPadding().imePadding().padding(horizontal = 8.dp, vertical = 8.dp),
            verticalAlignment = Alignment.Bottom,
        ) {
            IconButton(onAttach) { Icon(Icons.Outlined.AttachFile, "Attach images, videos or PDFs") }
            OutlinedTextField(
                value = text,
                onValueChange = { text = it },
                placeholder = { Text("Note, link or #tags") },
                maxLines = 6,
                shape = RoundedCornerShape(24.dp),
                modifier = Modifier.weight(1f),
            )
            AnimatedVisibility(text.isNotBlank()) {
                IconButton({ if (onSend(text)) text = "" }) {
                    Icon(Icons.AutoMirrored.Filled.Send, "Save", tint = MaterialTheme.colorScheme.primary)
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ItemActions(
    item: Item,
    onDismiss: () -> Unit,
    onPin: () -> Unit,
    onEdit: () -> Unit,
    onArchive: () -> Unit,
    onDelete: () -> Unit,
) {
    val context = LocalContext.current
    val clipboard = LocalClipboard.current
    val scope = rememberCoroutineScope()
    val shareText = listOfNotNull(item.url, item.text.ifBlank { null }).joinToString("\n\n")

    @Composable
    fun Action(label: String, icon: ImageVector, run: () -> Unit) = ListItem(
        headlineContent = { Text(label) },
        leadingContent = { Icon(icon, null) },
        modifier = Modifier.padding(horizontal = 8.dp).clickable { onDismiss(); run() },
    )

    ModalBottomSheet(onDismiss) {
        Action(if (item.pinned) "Unpin" else "Pin", Icons.Outlined.PushPin, onPin)
        Action("Edit", Icons.Outlined.Edit, onEdit)
        if (shareText.isNotEmpty()) {
            Action("Copy", Icons.Outlined.ContentCopy) {
                scope.launch { clipboard.setClipEntry(ClipEntry(ClipData.newPlainText("notaekinesis", shareText))) }
            }
            Action("Share", Icons.Outlined.Share) {
                val send = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, shareText)
                item.title?.let { send.putExtra(Intent.EXTRA_SUBJECT, it) }
                context.startActivity(Intent.createChooser(send, null))
            }
        }
        Action(if (item.archived) "Move out of the archive" else "Archive", if (item.archived) Icons.Outlined.Unarchive else Icons.Outlined.Archive, onArchive)
        Action("Delete", Icons.Outlined.Delete, onDelete)
        Box(Modifier.padding(bottom = 16.dp))
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun EditSheet(item: Item, onDismiss: () -> Unit, onSave: (String) -> Unit) {
    var text by rememberSaveable(item.id) { mutableStateOf(CaptureParser.toText(item)) }
    val canSave = CaptureParser.parse(text) != null || item.file != null
    ModalBottomSheet(onDismiss) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 16.dp).imePadding(), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Edit", style = MaterialTheme.typography.titleMedium)
            OutlinedTextField(
                value = text,
                onValueChange = { text = it },
                minLines = 4,
                maxLines = 14,
                supportingText = { Text("Markdown. A link on the first line, #tags anywhere.") },
                modifier = Modifier.fillMaxWidth(),
            )
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                TextButton(onDismiss) { Text("Cancel") }
                TextButton({ onSave(text); onDismiss() }, enabled = canSave) { Text("Save") }
            }
        }
    }
}

@Composable
private fun Empty(filter: Filter) {
    Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) {
        Text(
            when {
                filter.search.isNotBlank() || filter.tags.isNotEmpty() -> "Nothing matches."
                filter.archived -> "Nothing archived yet."
                else -> "Nothing saved yet. Type below, or share a link or text to notaekinesis from any app."
            },
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/**
 * What to hand Coil for a profile picture: a web address as it is, and a `data:` URI as its bytes.
 * Identity providers (and the dev user) may send an inline, URL-encoded SVG, which Coil's own
 * data-URI support doesn't read; its SVG decoder recognises the bytes.
 */
private fun pictureModel(picture: String): Any {
    if (!picture.startsWith("data:", ignoreCase = true)) return picture
    val comma = picture.indexOf(',')
    if (comma < 0) return picture
    val meta = picture.substring(5, comma)
    val payload = picture.substring(comma + 1)
    return try {
        if (meta.endsWith(";base64", ignoreCase = true)) android.util.Base64.decode(payload, android.util.Base64.DEFAULT)
        else java.net.URLDecoder.decode(payload.replace("+", "%2B"), "UTF-8").toByteArray()
    } catch (_: IllegalArgumentException) {
        picture
    }
}
