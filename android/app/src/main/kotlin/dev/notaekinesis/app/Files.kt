package dev.notaekinesis.app

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import androidx.browser.customtabs.CustomTabsIntent
import androidx.core.content.FileProvider
import androidx.core.net.toUri
import dev.notaekinesis.core.FileInfo
import dev.notaekinesis.core.api.UnauthorizedException
import dev.notaekinesis.core.api.await
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.Request
import okhttp3.RequestBody
import okio.BufferedSink
import okio.source
import java.io.File
import java.io.IOException

/** Kinds of file the server keeps (same list as the web app and the Windows client). */
val SupportedMimeTypes = arrayOf("image/*", "video/*", "application/pdf")

/** A file from another app (a share or the file picker), streamed straight into the upload. */
class SharedFile(private val context: Context, val uri: Uri) {
    val name: String
    val mime: String
    private val size: Long

    init {
        var name: String? = null
        var size = -1L
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst()) {
                c.getColumnIndex(OpenableColumns.DISPLAY_NAME).takeIf { it >= 0 && !c.isNull(it) }?.let { name = c.getString(it) }
                c.getColumnIndex(OpenableColumns.SIZE).takeIf { it >= 0 && !c.isNull(it) }?.let { size = c.getLong(it) }
            }
        }
        this.mime = context.contentResolver.getType(uri) ?: "application/octet-stream"
        val ext = MimeTypeMap.getSingleton().getExtensionFromMimeType(mime)
        this.name = name ?: (uri.lastPathSegment?.substringAfterLast('/') ?: "file").let { if (ext != null && '.' !in it) "$it.$ext" else it }
        this.size = size
    }

    fun body(): RequestBody = object : RequestBody() {
        override fun contentType(): MediaType? = mime.toMediaTypeOrNull()
        override fun contentLength() = size
        // This runs on OkHttp's thread, where anything but an IOException would crash the app.
        override fun writeTo(sink: BufferedSink) {
            val input = try {
                context.contentResolver.openInputStream(uri)
            } catch (_: SecurityException) {
                throw IOException("The app it came from didn't allow reading $name.")
            } catch (e: IllegalArgumentException) {
                throw IOException("Can't read $name.", e)
            } ?: throw IOException("Can't read $name.")
            input.source().use { sink.writeAll(it) }
        }
    }
}

/** Opens a web page in a Custom Tab (the user's browser, without leaving the app). */
fun Context.openLink(url: String) {
    try {
        CustomTabsIntent.Builder().setShowTitle(true).build().launchUrl(this, url.toUri())
    } catch (_: ActivityNotFoundException) {
        startActivity(Intent(Intent.ACTION_VIEW, url.toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}

/**
 * Downloads an attachment (once; it's kept in the cache) and opens it in a viewer app.
 * Throws IOException with a message fit to show.
 */
suspend fun Context.openAttachment(file: FileInfo) {
    val api = session.state.value.api ?: throw IOException("Sign in to open files.")
    val dir = File(cacheDir, "files/${file.id.filter { it.isLetterOrDigit() || it == '-' }}")
    val safeName = file.name.replace(Regex("[\\\\/:*?\"<>|]"), "_").ifBlank { "file" }
    val target = File(dir, safeName)
    if (!target.exists()) withContext(Dispatchers.IO) {
        dir.mkdirs()
        val res = api.http.newCall(Request.Builder().url(api.fileUrl(file.id)).build()).await()
        res.use {
            if (it.code == 401) throw UnauthorizedException()
            if (!it.isSuccessful) throw IOException("The server answered ${it.code}.")
            val tmp = File(dir, "$safeName.part")
            tmp.outputStream().use { out -> it.body.byteStream().copyTo(out) }
            tmp.renameTo(target)
        }
    }
    val uri = FileProvider.getUriForFile(this, "$packageName.files", target)
    val view = Intent(Intent.ACTION_VIEW).setDataAndType(uri, file.mime)
        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
    try {
        startActivity(Intent.createChooser(view, file.name).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    } catch (_: ActivityNotFoundException) {
        throw IOException("No app here can open ${file.name}.")
    }
}
