package dev.notaekinesis.core

import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import java.security.SecureRandom

enum class ItemKind { Note, Link, File }

data class FileInfo(val id: String, val name: String, val mime: String, val size: Long) {
    val isImage get() = mime.startsWith("image/")
    val isVideo get() = mime.startsWith("video/")
}

/** A note, link or file, as stored locally. Times are Unix epoch milliseconds. */
data class Item(
    val id: String,
    /** Markdown. For links and files it's the optional text that goes with them. */
    val text: String = "",
    val url: String? = null,
    val title: String? = null,
    val description: String? = null,
    val siteName: String? = null,
    val faviconUrl: String? = null,
    val file: FileInfo? = null,
    val pinned: Boolean = false,
    val archived: Boolean = false,
    val tags: List<String> = emptyList(),
    val createdAt: Long = 0,
    val updatedAt: Long = 0,
    val deletedAt: Long? = null,
    /** The server revision this version came from; 0 if it has never been synced. */
    val rev: Long = 0,
) {
    val kind: ItemKind
        get() = when {
            file != null -> ItemKind.File
            url != null -> ItemKind.Link
            else -> ItemKind.Note
        }

    val isDeleted get() = deletedAt != null

    companion object {
        fun new(text: String = "", url: String? = null, tags: Iterable<String> = emptyList(), title: String? = null): Item {
            val now = Clock.now()
            return Item(
                id = Ids.uuid7(),
                text = text,
                url = url,
                title = title,
                tags = Tags.normalize(tags),
                createdAt = now,
                updatedAt = now,
            )
        }
    }
}

data class TagCount(val tag: String, val count: Int)

data class ItemQuery(
    val search: String? = null,
    val tags: List<String> = emptyList(),
    val archived: Boolean = false,
    val limit: Int = 1000,
)

object Tags {
    /** Same rules as the server: trimmed, no leading '#', lower case, no duplicates. */
    fun normalize(tags: Iterable<String>): List<String> {
        val seen = LinkedHashSet<String>()
        for (raw in tags) {
            val tag = raw.trim().trimStart('#').trim().lowercase()
            if (tag.isNotEmpty()) seen += tag
        }
        return seen.toList()
    }
}

object Clock {
    /** Swappable in tests. */
    @Volatile
    var source: () -> Long = { System.currentTimeMillis() }

    fun now(): Long = source()
}

object Ids {
    private val random = SecureRandom()

    /** RFC 9562 UUIDv7: time-ordered, so ids sort by creation like the server's. */
    fun uuid7(now: Long = System.currentTimeMillis()): String {
        val bytes = ByteArray(16)
        random.nextBytes(bytes)
        for (i in 0 until 6) bytes[i] = (now ushr (8 * (5 - i))).toByte()
        bytes[6] = ((bytes[6].toInt() and 0x0f) or 0x70).toByte()
        bytes[8] = ((bytes[8].toInt() and 0x3f) or 0x80).toByte()
        val hex = bytes.joinToString("") { "%02x".format(it) }
        return "${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}"
    }
}

object ServerUrl {
    /** "notes.lan/" becomes "https://notes.lan"; anything that isn't http(s) is null. */
    fun normalize(input: String): String? {
        var raw = input.trim()
        if (raw.isEmpty()) return null
        if ("://" !in raw) raw = "https://$raw"
        val url = raw.toHttpUrlOrNull() ?: return null
        val port = if (url.port != HttpUrl.defaultPort(url.scheme)) ":${url.port}" else ""
        return "${url.scheme}://${url.host}$port${url.encodedPath.trimEnd('/')}"
    }
}
