package dev.notaekinesis.core.api

import dev.notaekinesis.core.FileInfo
import dev.notaekinesis.core.Item
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

// Wire types for the server API (see /api/docs). JSON names are camelCase.

val ApiJson = Json {
    ignoreUnknownKeys = true
    encodeDefaults = true
}

@Serializable
data class FileDto(val id: String, val name: String, val mime: String, val size: Long)

/** An item as the server returns it. */
@Serializable
data class ItemDto(
    val id: String,
    val kind: String,
    val text: String,
    val url: String? = null,
    val title: String? = null,
    val description: String? = null,
    val siteName: String? = null,
    val faviconUrl: String? = null,
    val file: FileDto? = null,
    val pinned: Boolean,
    val archived: Boolean,
    val tags: List<String>,
    val createdAt: Long,
    val updatedAt: Long,
    val deletedAt: Long? = null,
    val rev: Long,
) {
    fun toItem() = Item(
        id = id,
        text = text,
        url = url,
        title = title,
        description = description,
        siteName = siteName,
        faviconUrl = faviconUrl,
        file = file?.let { FileInfo(it.id, it.name, it.mime, it.size) },
        pinned = pinned,
        archived = archived,
        tags = tags,
        createdAt = createdAt,
        updatedAt = updatedAt,
        deletedAt = deletedAt,
        rev = rev,
    )
}

/** A full item as a client writes it. */
@Serializable
data class ItemInput(
    val id: String,
    val kind: String,
    val text: String,
    val url: String?,
    val title: String?,
    val description: String?,
    val pinned: Boolean,
    val archived: Boolean,
    val tags: List<String>,
    val createdAt: Long,
    val updatedAt: Long,
    val deletedAt: Long?,
    val fileId: String?,
) {
    companion object {
        fun from(item: Item) = ItemInput(
            id = item.id,
            kind = item.kind.name.lowercase(),
            text = item.text,
            url = item.url,
            title = item.title,
            description = item.description,
            pinned = item.pinned,
            archived = item.archived,
            tags = item.tags,
            createdAt = item.createdAt,
            updatedAt = item.updatedAt,
            deletedAt = item.deletedAt,
            fileId = item.file?.id,
        )
    }
}

@Serializable
data class Mutation(val mutationId: String, val item: ItemInput)

@Serializable
data class PushBody(val mutations: List<Mutation>)

@Serializable
data class MutationResult(val mutationId: String, val itemId: String, val status: String, val rev: Long)

@Serializable
data class PushResponse(val results: List<MutationResult>, val rev: Long)

@Serializable
data class PullResponse(val items: List<ItemDto>, val nextRev: Long, val hasMore: Boolean)

@Serializable
data class RevEvent(val rev: Long)

@Serializable
data class User(val id: String, val name: String, val email: String, val image: String? = null)

@Serializable
data class VerifyBody(val token: String)

@Serializable
data class SessionInfo(val token: String)

@Serializable
data class VerifyResponse(val session: SessionInfo, val user: User)
