package dev.notaekinesis.app

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.ErrorOutline
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.IntentCompat
import androidx.lifecycle.lifecycleScope
import dev.notaekinesis.app.ui.NotaekinesisTheme
import dev.notaekinesis.app.ui.plainText
import dev.notaekinesis.core.CaptureParser
import dev.notaekinesis.core.Item
import dev.notaekinesis.core.ShareReader
import dev.notaekinesis.core.api.UnauthorizedException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException

/**
 * The phone's basket. What's shared is saved at once (links and text locally, so it works
 * offline; files upload straight away), then the sheet offers a box for a note or #tags.
 */
class ShareActivity : ComponentActivity() {
    private sealed interface Phase {
        data object SignedOut : Phase
        data object Nothing : Phase
        data class Uploading(val count: Int) : Phase
        data class Saved(val items: List<Item>) : Phase
        data class Failed(val message: String) : Phase
    }

    private var phase by mutableStateOf<Phase>(Phase.Nothing)
    private var note by mutableStateOf("")
    private var finishing = false

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        if (!session.state.value.signedIn) session.resume()
        if (savedInstanceState == null) take(intent)

        setContent {
            NotaekinesisTheme { Sheet() }
        }
    }

    private fun take(intent: Intent) {
        if (!session.state.value.signedIn) {
            phase = Phase.SignedOut
            return
        }
        val files = sharedFiles(intent)
        if (files.isNotEmpty()) {
            upload(files)
            return
        }
        val capture = ShareReader.read(intent.getStringExtra(Intent.EXTRA_TEXT), intent.getStringExtra(Intent.EXTRA_SUBJECT))
        if (capture == null) {
            phase = Phase.Nothing
            return
        }
        val item = session.store.save(Item.new(capture.text, capture.url, title = capture.title))
        phase = Phase.Saved(listOf(item))
    }

    private fun sharedFiles(intent: Intent): List<Uri> = when (intent.action) {
        Intent.ACTION_SEND -> listOfNotNull(IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java))
        Intent.ACTION_SEND_MULTIPLE -> IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
        else -> emptyList()
    }

    private fun upload(uris: List<Uri>) {
        phase = Phase.Uploading(uris.size)
        lifecycleScope.launch {
            val saved = mutableListOf<Item>()
            try {
                for (uri in uris) {
                    val file = withContext(Dispatchers.IO) { SharedFile(this@ShareActivity, uri) }
                    saved += session.saveFile(file.body(), file.name)
                }
                phase = Phase.Saved(saved)
            } catch (e: UnauthorizedException) {
                phase = Phase.SignedOut
            } catch (e: IOException) {
                val done = if (saved.isEmpty()) "" else " (${saved.size} of ${uris.size} saved)"
                phase = Phase.Failed((e.message ?: "Couldn't upload.") + done)
            } catch (e: SecurityException) {
                phase = Phase.Failed("That app didn't allow reading the file.")
            }
        }
    }

    /** Adds what was typed (a note and/or #tags) to everything just saved, then closes. */
    private fun done() {
        if (finishing) return
        finishing = true
        val saved = (phase as? Phase.Saved)?.items.orEmpty()
        val extra = CaptureParser.parse(note)
        if (extra != null) {
            val typed = listOfNotNull(extra.url, extra.text.ifBlank { null }).joinToString("\n\n")
            for (item in saved) {
                val current = session.store.get(item.id) ?: item
                session.store.save(
                    current.copy(
                        text = listOf(current.text, typed).filter { it.isNotBlank() }.joinToString("\n\n"),
                        tags = current.tags + extra.tags,
                    ),
                )
            }
        }
        // The app's own sync usually gets there first; this covers Android stopping the process.
        if (session.store.pendingCount > 0) SyncWorker.runSoon(this)
        finish()
        if (Build.VERSION.SDK_INT >= 34) overrideActivityTransition(OVERRIDE_TRANSITION_CLOSE, 0, 0)
    }

    @OptIn(ExperimentalMaterial3Api::class)
    @Composable
    private fun Sheet() {
        ModalBottomSheet(onDismissRequest = ::done) {
            Column(
                Modifier.fillMaxWidth().padding(horizontal = 20.dp).padding(bottom = 20.dp).imePadding(),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                when (val p = phase) {
                    Phase.SignedOut -> {
                        Status(Icons.Outlined.ErrorOutline, "Sign in to notaekinesis first.")
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                            TextButton(::done) { Text("Cancel") }
                            Button({
                                startActivity(Intent(this@ShareActivity, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                                done()
                            }) { Text("Open notaekinesis") }
                        }
                    }
                    Phase.Nothing -> {
                        Status(Icons.Outlined.ErrorOutline, "Nothing to save here.")
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) { TextButton(::done) { Text("Close") } }
                    }
                    is Phase.Uploading -> Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                        Text(if (p.count == 1) "Uploading…" else "Uploading ${p.count} files…", style = MaterialTheme.typography.titleMedium)
                    }
                    is Phase.Failed -> {
                        Status(Icons.Outlined.ErrorOutline, p.message)
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                            TextButton(::done) { Text("Close") }
                            Button({ upload(sharedFiles(intent)) }) { Text("Try again") }
                        }
                    }
                    is Phase.Saved -> {
                        Status(Icons.Outlined.CheckCircle, if (p.items.size == 1) "Saved" else "Saved ${p.items.size} files")
                        Text(
                            describe(p.items),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 3,
                            overflow = TextOverflow.Ellipsis,
                        )
                        OutlinedTextField(
                            value = note,
                            onValueChange = { note = it },
                            placeholder = { Text("Add a note or #tags") },
                            maxLines = 4,
                            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                            keyboardActions = KeyboardActions(onDone = { done() }),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) { Button(::done) { Text("Done") } }
                    }
                }
            }
        }
    }

    @Composable
    private fun Status(icon: androidx.compose.ui.graphics.vector.ImageVector, text: String) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Icon(icon, null, tint = MaterialTheme.colorScheme.primary)
            Text(text, style = MaterialTheme.typography.titleMedium)
        }
    }

    private fun describe(items: List<Item>): String = items.joinToString(", ") { item ->
        item.file?.name ?: item.title ?: item.url ?: plainText(item.text)
    }
}
