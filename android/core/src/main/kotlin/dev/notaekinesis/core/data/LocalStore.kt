package dev.notaekinesis.core.data

import androidx.sqlite.SQLiteConnection
import androidx.sqlite.SQLiteDriver
import androidx.sqlite.SQLiteStatement
import dev.notaekinesis.core.Clock
import dev.notaekinesis.core.FileInfo
import dev.notaekinesis.core.Ids
import dev.notaekinesis.core.Item
import dev.notaekinesis.core.ItemQuery
import dev.notaekinesis.core.TagCount
import dev.notaekinesis.core.Tags
import dev.notaekinesis.core.api.ApiJson
import dev.notaekinesis.core.api.ItemInput
import dev.notaekinesis.core.api.Mutation
import dev.notaekinesis.core.api.MutationResult
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.builtins.serializer

/**
 * The local copy of the user's items plus the outbox of changes not yet pushed. Every local
 * write goes to both in one transaction, so the UI never waits for the network. One connection,
 * guarded by a lock: SQLite is fast enough that callers never wait long. Same schema and SQL as
 * the Windows client's LocalStore (it needs bundled SQLite for FTS5).
 */
class LocalStore(driver: SQLiteDriver, path: String) : AutoCloseable {
    private val db: SQLiteConnection = driver.open(path)
    private val lock = Any()

    private val _changes = MutableStateFlow(0L)

    /** Bumped after any change to items, local or from sync. */
    val changes: StateFlow<Long> = _changes.asStateFlow()

    private val _localWrites = MutableSharedFlow<Unit>(extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)

    /** Emits after a local write, so sync can push it. */
    val localWrites: SharedFlow<Unit> = _localWrites.asSharedFlow()

    init {
        query("PRAGMA journal_mode = WAL") { it.step() }
        exec("PRAGMA synchronous = NORMAL")
        migrate()
    }

    override fun close() = synchronized(lock) { db.close() }

    // --- schema -------------------------------------------------------------------

    private fun migrate() {
        val version = query("PRAGMA user_version") { it.step(); it.getLong(0).toInt() }
        for (i in version until migrations.size) {
            transaction {
                migrations[i].forEach { exec(it) }
                exec("PRAGMA user_version = ${i + 1}")
            }
        }
    }

    // --- reads --------------------------------------------------------------------

    fun get(id: String): Item? = synchronized(lock) { getUnlocked(id) }

    private fun getUnlocked(id: String): Item? =
        query("SELECT $COLUMNS FROM items i WHERE i.id = ?", id) { if (it.step()) readItem(it) else null }

    /** Pinned first, then newest. Search matches word prefixes, ignoring accents. */
    fun query(q: ItemQuery): List<Item> {
        val sql = StringBuilder("SELECT $COLUMNS FROM items i WHERE i.deleted_at IS NULL AND i.archived = ?")
        val args = mutableListOf<Any?>(if (q.archived) 1L else 0L)
        val fts = q.search?.let(::toFtsQuery)
        if (fts != null) {
            sql.append(" AND i.id IN (SELECT item_id FROM items_fts WHERE items_fts MATCH ?)")
            args += fts
        }
        for (tag in q.tags) {
            sql.append(" AND EXISTS (SELECT 1 FROM item_tags t WHERE t.item_id = i.id AND t.tag = ?)")
            args += tag
        }
        sql.append(" ORDER BY i.pinned DESC, i.created_at DESC, i.id DESC LIMIT ?")
        args += q.limit.toLong()
        return synchronized(lock) {
            query(sql.toString(), *args.toTypedArray()) { st ->
                buildList { while (st.step()) add(readItem(st)) }
            }
        }
    }

    /** Tags of items that aren't deleted, most used first. */
    fun tags(archived: Boolean = false): List<TagCount> = synchronized(lock) {
        query(
            """
            SELECT t.tag, count(*) AS n FROM item_tags t JOIN items i ON i.id = t.item_id
            WHERE i.deleted_at IS NULL AND i.archived = ?
            GROUP BY t.tag ORDER BY n DESC, t.tag
            """,
            if (archived) 1L else 0L,
        ) { st -> buildList { while (st.step()) add(TagCount(st.getText(0), st.getLong(1).toInt())) } }
    }

    // --- local writes ---------------------------------------------------------------

    /** Saves an item changed on this device and queues it for push. Returns the item as stored. */
    fun save(item: Item): Item {
        val saved = synchronized(lock) {
            transaction {
                val existing = getUnlocked(item.id)
                // Always newer than the version it was edited from, so a slow clock can't make our own edit lose.
                val updatedAt = maxOf(Clock.now(), (existing?.updatedAt ?: 0) + 1)
                val saved = item.copy(tags = Tags.normalize(item.tags), updatedAt = updatedAt, rev = existing?.rev ?: 0)
                upsert(saved)
                val mutation = Mutation(Ids.uuid7(), ItemInput.from(saved))
                exec(
                    "INSERT OR REPLACE INTO outbox (item_id, mutation_id, payload) VALUES (?, ?, ?)",
                    saved.id, mutation.mutationId, ApiJson.encodeToString(Mutation.serializer(), mutation),
                )
                saved
            }
        }
        _changes.value++
        _localWrites.tryEmit(Unit)
        return saved
    }

    fun delete(item: Item): Item = save(item.copy(deletedAt = Clock.now()))

    // --- sync -----------------------------------------------------------------------

    val lastRev: Long
        get() = synchronized(lock) {
            query("SELECT value FROM meta WHERE key = 'last_rev'") { if (it.step()) it.getText(0).toLong() else 0L }
        }

    val pendingCount: Int
        get() = synchronized(lock) { query("SELECT count(*) FROM outbox") { it.step(); it.getLong(0).toInt() } }

    fun pendingMutations(max: Int = 500): List<Mutation> = synchronized(lock) {
        query("SELECT payload FROM outbox ORDER BY mutation_id LIMIT ?", max.toLong()) { st ->
            buildList { while (st.step()) add(ApiJson.decodeFromString(Mutation.serializer(), st.getText(0))) }
        }
    }

    /**
     * Clears pushed mutations. An item edited again while the push was in flight keeps its
     * newer outbox entry, since that has a different mutation id.
     */
    fun completePush(results: List<MutationResult>) = synchronized(lock) {
        transaction {
            for (res in results) {
                exec("DELETE FROM outbox WHERE item_id = ? AND mutation_id = ?", res.itemId, res.mutationId)
                if (res.status == "applied") exec("UPDATE items SET rev = max(rev, ?) WHERE id = ?", res.rev, res.itemId)
            }
        }
    }

    /**
     * Applies pulled items and moves the sync position forward, in one transaction. A local edit
     * that is still waiting to be pushed and is newer than the incoming version is kept: the server
     * will take it on the next push (last writer wins).
     */
    fun applyRemote(items: List<Item>, nextRev: Long) {
        var changed = false
        synchronized(lock) {
            transaction {
                for (remote in items) {
                    val pending = query("SELECT 1 FROM outbox WHERE item_id = ?", remote.id) { it.step() }
                    val local = if (pending) getUnlocked(remote.id) else null
                    if (local != null && local.updatedAt > remote.updatedAt) continue
                    if (pending) exec("DELETE FROM outbox WHERE item_id = ?", remote.id)
                    upsert(remote)
                    changed = true
                }
                exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('last_rev', ?)", nextRev.toString())
            }
        }
        if (changed) _changes.value++
    }

    /** Forgets everything, e.g. when signing in to a different account or server. */
    fun reset() {
        synchronized(lock) {
            transaction {
                for (table in listOf("items", "item_tags", "items_fts", "outbox", "meta")) exec("DELETE FROM $table")
            }
        }
        _changes.value++
    }

    // --- helpers --------------------------------------------------------------------

    private fun upsert(item: Item) {
        exec(
            """
            INSERT OR REPLACE INTO items (id, text, url, title, description, site_name, favicon_url, file_id, file_name,
              file_mime, file_size, pinned, archived, tags, created_at, updated_at, deleted_at, rev)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            item.id, item.text, item.url, item.title, item.description, item.siteName, item.faviconUrl,
            item.file?.id, item.file?.name, item.file?.mime, item.file?.size,
            if (item.pinned) 1L else 0L, if (item.archived) 1L else 0L,
            ApiJson.encodeToString(TAGS, item.tags), item.createdAt, item.updatedAt, item.deletedAt, item.rev,
        )
        exec("DELETE FROM item_tags WHERE item_id = ?", item.id)
        exec("DELETE FROM items_fts WHERE item_id = ?", item.id)
        if (item.isDeleted) return
        for (tag in item.tags) exec("INSERT OR IGNORE INTO item_tags (item_id, tag) VALUES (?, ?)", item.id, tag)
        exec(
            "INSERT INTO items_fts (item_id, text, title, description, url, file_name) VALUES (?, ?, ?, ?, ?, ?)",
            item.id, item.text, item.title ?: "", item.description ?: "", item.url ?: "", item.file?.name ?: "",
        )
    }

    private fun readItem(st: SQLiteStatement): Item {
        fun str(i: Int) = if (st.isNull(i)) null else st.getText(i)
        return Item(
            id = st.getText(0),
            text = st.getText(1),
            url = str(2),
            title = str(3),
            description = str(4),
            siteName = str(5),
            faviconUrl = str(6),
            file = if (st.isNull(7)) null else FileInfo(st.getText(7), str(8) ?: "file", str(9) ?: "", if (st.isNull(10)) 0 else st.getLong(10)),
            pinned = st.getLong(11) != 0L,
            archived = st.getLong(12) != 0L,
            tags = ApiJson.decodeFromString(TAGS, st.getText(13)),
            createdAt = st.getLong(14),
            updatedAt = st.getLong(15),
            deletedAt = if (st.isNull(16)) null else st.getLong(16),
            rev = st.getLong(17),
        )
    }

    private inline fun <T> transaction(block: () -> T): T {
        exec("BEGIN IMMEDIATE")
        try {
            val result = block()
            exec("COMMIT")
            return result
        } catch (e: Throwable) {
            exec("ROLLBACK")
            throw e
        }
    }

    private inline fun <T> query(sql: String, vararg args: Any?, read: (SQLiteStatement) -> T): T =
        db.prepare(sql).use { st ->
            args.forEachIndexed { i, arg ->
                when (arg) {
                    null -> st.bindNull(i + 1)
                    is String -> st.bindText(i + 1, arg)
                    is Long -> st.bindLong(i + 1, arg)
                    is Int -> st.bindLong(i + 1, arg.toLong())
                    else -> error("Unsupported parameter ${arg::class}")
                }
            }
            read(st)
        }

    private fun exec(sql: String, vararg args: Any?) = query(sql, *args) { it.step() }

    companion object {
        private val migrations = listOf(
            listOf(
                """
                CREATE TABLE items (
                  id TEXT PRIMARY KEY,
                  text TEXT NOT NULL,
                  url TEXT,
                  title TEXT,
                  description TEXT,
                  site_name TEXT,
                  favicon_url TEXT,
                  file_id TEXT,
                  file_name TEXT,
                  file_mime TEXT,
                  file_size INTEGER,
                  pinned INTEGER NOT NULL,
                  archived INTEGER NOT NULL,
                  tags TEXT NOT NULL,
                  created_at INTEGER NOT NULL,
                  updated_at INTEGER NOT NULL,
                  deleted_at INTEGER,
                  rev INTEGER NOT NULL
                )
                """,
                "CREATE INDEX items_list ON items (archived, pinned DESC, created_at DESC) WHERE deleted_at IS NULL",
                "CREATE TABLE item_tags (item_id TEXT NOT NULL, tag TEXT NOT NULL, PRIMARY KEY (tag, item_id)) WITHOUT ROWID",
                """
                CREATE VIRTUAL TABLE items_fts USING fts5(
                  item_id UNINDEXED, text, title, description, url, file_name,
                  tokenize = 'unicode61 remove_diacritics 2'
                )
                """,
                // One pending change per item: a later edit replaces the earlier one.
                "CREATE TABLE outbox (item_id TEXT PRIMARY KEY, mutation_id TEXT NOT NULL, payload TEXT NOT NULL)",
                "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)",
            ),
        )

        private val TAGS = ListSerializer(String.serializer())

        private const val COLUMNS =
            "i.id, i.text, i.url, i.title, i.description, i.site_name, i.favicon_url, i.file_id, i.file_name, " +
                "i.file_mime, i.file_size, i.pinned, i.archived, i.tags, i.created_at, i.updated_at, i.deleted_at, i.rev"

        private val WORD = Regex("[\\p{L}\\p{N}]+")

        /** Free text to an FTS5 prefix query, like the server's. Null if there's nothing to search for. */
        fun toFtsQuery(q: String): String? {
            val tokens = WORD.findAll(q).map { "\"${it.value}\"*" }.toList()
            return if (tokens.isEmpty()) null else tokens.joinToString(" ")
        }
    }
}
