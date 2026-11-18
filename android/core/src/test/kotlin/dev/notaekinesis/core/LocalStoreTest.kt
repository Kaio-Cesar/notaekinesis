package dev.notaekinesis.core

import androidx.sqlite.driver.bundled.BundledSQLiteDriver
import dev.notaekinesis.core.api.MutationResult
import dev.notaekinesis.core.data.LocalStore
import java.nio.file.Files
import kotlin.io.path.ExperimentalPathApi
import kotlin.io.path.deleteRecursively
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class LocalStoreTest {
    private val dir = Files.createTempDirectory("notaekinesis-")
    private val store = LocalStore(BundledSQLiteDriver(), dir.resolve("test.db").toString())

    @OptIn(ExperimentalPathApi::class)
    @AfterTest
    fun cleanup() {
        store.close()
        Clock.source = { System.currentTimeMillis() }
        dir.deleteRecursively()
    }

    @Test
    fun saving_queues_one_mutation_per_item() {
        val item = store.save(Item.new("first", tags = listOf("#Work", "work", " ideas ")))
        store.save(item.copy(text = "second"))

        val pending = store.pendingMutations().single()
        assertEquals("second", pending.item.text)
        assertEquals(listOf("work", "ideas"), pending.item.tags)
        assertEquals("note", pending.item.kind)
    }

    @Test
    fun search_matches_word_prefixes_ignoring_accents_and_tags_filter() {
        store.save(Item.new("Café com pão", tags = listOf("food")))
        store.save(Item.new(url = "https://example.com/docs", tags = listOf("work")))
        store.save(Item.new("unrelated"))

        assertEquals("Café com pão", store.query(ItemQuery("cafe pa")).single().text)
        assertEquals(ItemKind.Link, store.query(ItemQuery("exam")).single().kind)
        assertEquals(1, store.query(ItemQuery(tags = listOf("work"))).size)
        assertEquals(3, store.query(ItemQuery("  ")).size)
        assertEquals(listOf(TagCount("food", 1), TagCount("work", 1)), store.tags())
    }

    @Test
    fun pinned_first_and_deleted_or_archived_items_are_hidden() {
        Clock.source = { 1000 }
        val old = store.save(Item.new("old").copy(pinned = true))
        Clock.source = { 2000 }
        store.save(Item.new("archived").copy(archived = true))
        val gone = store.save(Item.new("gone"))
        store.save(Item.new("new"))
        store.delete(gone)
        Clock.source = { System.currentTimeMillis() }

        assertEquals(listOf("old", "new"), store.query(ItemQuery()).map { it.text })
        assertEquals("archived", store.query(ItemQuery(archived = true)).single().text)
        assertTrue(store.query(ItemQuery("gone")).isEmpty())
        assertNotNull(store.get(gone.id)!!.deletedAt)
        assertEquals(old.id, store.query(ItemQuery())[0].id)
    }

    @Test
    fun an_edit_is_always_newer_than_the_version_it_came_from_even_with_a_slow_clock() {
        val remote = Item.new("from phone").copy(updatedAt = System.currentTimeMillis() + 60_000, rev = 5)
        store.applyRemote(listOf(remote), 5)

        val edited = store.save(store.get(remote.id)!!.copy(text = "edited here"))
        assertEquals(remote.updatedAt + 1, edited.updatedAt)
        assertEquals(5, edited.rev)
    }

    @Test
    fun pull_keeps_a_newer_pending_local_edit_and_replaces_an_older_one() {
        val item = store.save(Item.new("local"))

        // An older remote version doesn't overwrite the pending edit.
        store.applyRemote(listOf(item.copy(text = "older remote", updatedAt = item.updatedAt - 10, rev = 3)), 3)
        assertEquals("local", store.get(item.id)!!.text)
        assertEquals(1, store.pendingCount)
        assertEquals(3, store.lastRev)

        // A newer one wins, and the pending edit is dropped.
        store.applyRemote(listOf(item.copy(text = "newer remote", updatedAt = item.updatedAt + 10, rev = 4)), 4)
        assertEquals("newer remote", store.get(item.id)!!.text)
        assertEquals(0, store.pendingCount)
    }

    @Test
    fun completing_a_push_keeps_edits_made_while_it_was_in_flight() {
        val item = store.save(Item.new("v1"))
        val sent = store.pendingMutations()
        store.save(item.copy(text = "v2")) // while the push is in flight

        store.completePush(listOf(MutationResult(sent[0].mutationId, item.id, "applied", 7)))

        assertEquals("v2", store.pendingMutations().single().item.text)
        assertEquals(7, store.get(item.id)!!.rev)
    }

    @Test
    fun files_round_trip_and_reset_forgets_everything() {
        val saved = store.save(Item.new().copy(file = FileInfo("f1", "photo.jpg", "image/jpeg", 1234)))
        assertEquals(ItemKind.File, store.get(saved.id)!!.kind)
        assertEquals("file", store.pendingMutations().single().item.kind)
        assertEquals(1, store.query(ItemQuery("photo")).size)

        store.reset()
        assertNull(store.get(saved.id))
        assertEquals(0, store.pendingCount)
    }

    @Test
    fun server_urls_are_normalized() {
        assertEquals("https://notes.lan", ServerUrl.normalize(" notes.lan/ "))
        assertEquals("http://localhost:3000", ServerUrl.normalize("http://localhost:3000"))
        assertEquals("https://lab.example/notes", ServerUrl.normalize("https://lab.example/notes/"))
        assertNull(ServerUrl.normalize("ftp://x"))
        assertNull(ServerUrl.normalize(""))
    }

    @Test
    fun uuid7_ids_sort_by_time() {
        val a = Ids.uuid7(1_000)
        val b = Ids.uuid7(2_000)
        assertTrue(a < b)
        assertEquals('7', a[14])
    }
}
