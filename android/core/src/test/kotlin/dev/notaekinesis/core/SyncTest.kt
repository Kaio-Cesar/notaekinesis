package dev.notaekinesis.core

import androidx.sqlite.driver.bundled.BundledSQLiteDriver
import dev.notaekinesis.core.api.ApiClient
import dev.notaekinesis.core.api.ApiJson
import dev.notaekinesis.core.api.ItemDto
import dev.notaekinesis.core.api.MutationResult
import dev.notaekinesis.core.api.PullResponse
import dev.notaekinesis.core.api.PushBody
import dev.notaekinesis.core.api.PushResponse
import dev.notaekinesis.core.api.UnauthorizedException
import dev.notaekinesis.core.data.LocalStore
import dev.notaekinesis.core.sync.SyncEngine
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import java.nio.file.Files
import kotlin.io.path.deleteRecursively
import kotlin.io.path.ExperimentalPathApi
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

/** A tiny stand-in for the server's sync endpoints, with last-writer-wins like the real one. */
class FakeServer : Interceptor {
    val items = LinkedHashMap<String, ItemDto>()
    var rev = 0L
    var lastAuth: String? = null

    override fun intercept(chain: Interceptor.Chain): Response {
        val req = chain.request()
        lastAuth = req.header("Authorization")
        fun respond(code: Int, body: String, type: String = "application/json") = Response.Builder()
            .request(req).protocol(Protocol.HTTP_1_1).code(code).message("")
            .body(body.toResponseBody(type.toMediaType())).build()

        return when (req.url.encodedPath) {
            "/api/sync/push" -> {
                val buffer = Buffer().also { req.body!!.writeTo(it) }
                val body = ApiJson.decodeFromString(PushBody.serializer(), buffer.readUtf8())
                val results = body.mutations.map { m ->
                    val cur = items[m.item.id]
                    if (cur != null && cur.updatedAt >= m.item.updatedAt) {
                        MutationResult(m.mutationId, m.item.id, "stale", cur.rev)
                    } else {
                        val i = m.item
                        items[i.id] = ItemDto(i.id, i.kind, i.text, i.url, i.title, i.description, pinned = i.pinned,
                            archived = i.archived, tags = i.tags, createdAt = i.createdAt, updatedAt = i.updatedAt,
                            deletedAt = i.deletedAt, rev = ++rev)
                        MutationResult(m.mutationId, i.id, "applied", rev)
                    }
                }
                respond(200, ApiJson.encodeToString(PushResponse.serializer(), PushResponse(results, rev)))
            }
            "/api/sync/pull" -> {
                val since = req.url.queryParameter("since")!!.toLong()
                val limit = req.url.queryParameter("limit")!!.toInt()
                val page = items.values.filter { it.rev > since }.sortedBy { it.rev }.take(limit + 1).toMutableList()
                val more = page.size > limit
                if (more) page.removeAt(page.size - 1)
                val next = if (page.isNotEmpty()) page.last().rev else maxOf(since, rev)
                respond(200, ApiJson.encodeToString(PullResponse.serializer(), PullResponse(page, next, more)))
            }
            "/api/sync/events" -> respond(
                200,
                "retry: 5000\nevent: rev\ndata: {\"rev\":1}\n\n: ping\n\nevent: rev\ndata: {\"rev\":2}\n\n",
                "text/event-stream",
            )
            else -> respond(401, "")
        }
    }
}

class SyncTest {
    private val dir = Files.createTempDirectory("notaekinesis-")
    private val server = FakeServer()
    private val stores = mutableListOf<LocalStore>()

    @OptIn(ExperimentalPathApi::class)
    @AfterTest
    fun cleanup() {
        stores.forEach { it.close() }
        Clock.source = { System.currentTimeMillis() }
        dir.deleteRecursively()
    }

    private fun api(token: String) = ApiClient("http://server.test", token, OkHttpClient.Builder().addInterceptor(server).build())

    private fun kotlinx.coroutines.test.TestScope.device(name: String): Pair<LocalStore, SyncEngine> {
        val store = LocalStore(BundledSQLiteDriver(), dir.resolve("$name.db").toString()).also { stores += it }
        return store to SyncEngine(store, api("token-$name"), backgroundScope)
    }

    @Test
    fun two_devices_converge_and_deletes_propagate() = runTest {
        val (laptop, laptopSync) = device("laptop")
        val (desktop, desktopSync) = device("desktop")
        val note = laptop.save(Item.new("buy milk", tags = listOf("home")))
        laptop.save(Item.new(url = "https://example.com"))
        laptopSync.syncOnce()
        assertEquals(0, laptop.pendingCount)
        assertEquals("Bearer token-laptop", server.lastAuth)

        desktopSync.syncOnce()
        assertEquals(2, desktop.query(ItemQuery()).size)

        desktop.delete(desktop.get(note.id)!!)
        desktopSync.syncOnce()
        laptopSync.syncOnce()

        assertEquals(1, laptop.query(ItemQuery()).size)
        assertEquals(server.rev, laptop.lastRev)
    }

    @Test
    fun offline_edits_on_both_sides_resolve_to_the_newest() = runTest {
        val (a, aSync) = device("a")
        val (b, bSync) = device("b")
        val item = a.save(Item.new("original"))
        aSync.syncOnce()
        bSync.syncOnce()

        val t = item.updatedAt
        Clock.source = { t + 100 }
        a.save(a.get(item.id)!!.copy(text = "edit on a"))
        Clock.source = { t + 200 }
        b.save(b.get(item.id)!!.copy(text = "edit on b (later)"))
        Clock.source = { System.currentTimeMillis() }

        bSync.syncOnce()
        aSync.syncOnce() // a's push is stale; the pull brings b's
        bSync.syncOnce()

        assertEquals("edit on b (later)", a.get(item.id)!!.text)
        assertEquals("edit on b (later)", b.get(item.id)!!.text)
        assertEquals(0, a.pendingCount)
    }

    @Test
    fun pull_pages_through_everything() = runTest {
        repeat(1203) { n ->
            val id = Ids.uuid7()
            server.items[id] = ItemDto(id, "note", "n$n", pinned = false, archived = false, tags = emptyList(),
                createdAt = n.toLong(), updatedAt = n.toLong(), rev = ++server.rev)
        }
        val (store, sync) = device("big")
        sync.syncOnce()
        assertEquals(1203, store.query(ItemQuery(limit = 5000)).size)
        assertEquals(1203, store.lastRev)
    }

    @Test
    fun event_stream_yields_revisions_and_skips_heartbeats() = runTest {
        assertEquals(listOf(1L, 2L), api("t").events().toList())
    }

    @Test
    fun a_revoked_session_reports_signed_out() = runTest {
        assertFailsWith<UnauthorizedException> { api("t").me() }
    }
}
