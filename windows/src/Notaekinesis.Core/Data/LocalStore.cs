using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Notaekinesis.Core.Api;

namespace Notaekinesis.Core.Data;

/// <summary>
/// The local copy of the user's items plus the outbox of changes not yet pushed.
/// Every local write goes to both in one transaction, so the UI never waits for the network.
/// One connection, guarded by a lock: SQLite is fast enough that callers never wait long.
/// </summary>
public sealed partial class LocalStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();
    private SqliteTransaction? _tx;

    /// <summary>Raised after any change to items, local or from sync. May fire on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after a local write, so sync can push it.</summary>
    public event EventHandler? LocalWrite;

    public LocalStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;");
        Migrate();
    }

    public void Dispose() => _db.Dispose();

    // --- schema -----------------------------------------------------------------

    private static readonly string[] Migrations =
    [
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
        );
        CREATE INDEX items_list ON items (archived, pinned DESC, created_at DESC) WHERE deleted_at IS NULL;
        CREATE TABLE item_tags (item_id TEXT NOT NULL, tag TEXT NOT NULL, PRIMARY KEY (tag, item_id)) WITHOUT ROWID;
        CREATE VIRTUAL TABLE items_fts USING fts5(
          item_id UNINDEXED, text, title, description, url, file_name,
          tokenize = 'unicode61 remove_diacritics 2'
        );
        -- One pending change per item: a later edit replaces the earlier one.
        CREATE TABLE outbox (item_id TEXT PRIMARY KEY, mutation_id TEXT NOT NULL, payload TEXT NOT NULL);
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
    ];

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), System.Globalization.CultureInfo.InvariantCulture);
        for (var i = version; i < Migrations.Length; i++)
        {
            using var tx = Begin();
            Exec(Migrations[i]);
            Exec($"PRAGMA user_version = {i + 1}");
            tx.Commit();
        }
    }

    // --- reads ------------------------------------------------------------------

    private const string Columns =
        "i.id, i.text, i.url, i.title, i.description, i.site_name, i.favicon_url, i.file_id, i.file_name, "
        + "i.file_mime, i.file_size, i.pinned, i.archived, i.tags, i.created_at, i.updated_at, i.deleted_at, i.rev";

    public Item? Get(string id)
    {
        lock (_lock) return GetUnlocked(id);
    }

    private Item? GetUnlocked(string id)
    {
        using var cmd = Command($"SELECT {Columns} FROM items i WHERE i.id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadItem(r) : null;
    }

    /// <summary>Pinned first, then newest. Search matches word prefixes, ignoring accents.</summary>
    public IReadOnlyList<Item> Query(ItemQuery query)
    {
        var sql = new StringBuilder($"SELECT {Columns} FROM items i WHERE i.deleted_at IS NULL AND i.archived = $archived");
        var args = new List<(string, object?)> { ("$archived", query.Archived ? 1 : 0) };

        var fts = query.Search is null ? null : ToFtsQuery(query.Search);
        if (fts is not null)
        {
            sql.Append(" AND i.id IN (SELECT item_id FROM items_fts WHERE items_fts MATCH $q)");
            args.Add(("$q", fts));
        }
        var tags = query.Tags ?? [];
        for (var n = 0; n < tags.Count; n++)
        {
            sql.Append($" AND EXISTS (SELECT 1 FROM item_tags t WHERE t.item_id = i.id AND t.tag = $t{n})");
            args.Add(($"$t{n}", tags[n]));
        }
        sql.Append(" ORDER BY i.pinned DESC, i.created_at DESC, i.id DESC LIMIT $limit");
        args.Add(("$limit", query.Limit));

        lock (_lock)
        {
            using var cmd = Command(sql.ToString(), [.. args]);
            using var r = cmd.ExecuteReader();
            var items = new List<Item>();
            while (r.Read()) items.Add(ReadItem(r));
            return items;
        }
    }

    /// <summary>Tags of items that aren't deleted, most used first.</summary>
    public IReadOnlyList<TagCount> Tags(bool archived = false)
    {
        lock (_lock)
        {
            using var cmd = Command(
                """
                SELECT t.tag, count(*) AS n FROM item_tags t JOIN items i ON i.id = t.item_id
                WHERE i.deleted_at IS NULL AND i.archived = $archived
                GROUP BY t.tag ORDER BY n DESC, t.tag
                """,
                ("$archived", archived ? 1 : 0)
            );
            using var r = cmd.ExecuteReader();
            var tags = new List<TagCount>();
            while (r.Read()) tags.Add(new TagCount(r.GetString(0), r.GetInt32(1)));
            return tags;
        }
    }

    // --- local writes -------------------------------------------------------------

    /// <summary>
    /// Saves an item changed on this device and queues it for push. Deleting is
    /// <c>item with { DeletedAt = ... }</c>. Returns the item as stored.
    /// </summary>
    public Item Save(Item item)
    {
        Item saved;
        lock (_lock)
        {
            using var tx = Begin();
            var existing = GetUnlocked(item.Id);
            // Always newer than the version it was edited from, so a slow clock can't make our own edit lose.
            var updatedAt = Math.Max(Clock.Now(), (existing?.UpdatedAt ?? 0) + 1);
            saved = item with
            {
                Tags = Tag.Normalize(item.Tags),
                UpdatedAt = updatedAt,
                Rev = existing?.Rev ?? 0,
            };
            Upsert(saved);
            var mutation = new Mutation(Guid.CreateVersion7().ToString(), ItemInput.From(saved));
            Exec(
                "INSERT OR REPLACE INTO outbox (item_id, mutation_id, payload) VALUES ($id, $m, $p)",
                ("$id", saved.Id),
                ("$m", mutation.MutationId),
                ("$p", JsonSerializer.Serialize(mutation, ApiJson.Default.Mutation))
            );
            tx.Commit();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        LocalWrite?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public Item Delete(Item item) => Save(item with { DeletedAt = Clock.Now() });

    // --- sync ---------------------------------------------------------------------

    public long LastRev
    {
        get
        {
            lock (_lock)
                return Scalar("SELECT value FROM meta WHERE key = 'last_rev'") is string s ? long.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_lock) return Convert.ToInt32(Scalar("SELECT count(*) FROM outbox"), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<Mutation> PendingMutations(int max = 500)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT payload FROM outbox ORDER BY mutation_id LIMIT $max", ("$max", max));
            using var r = cmd.ExecuteReader();
            var list = new List<Mutation>();
            while (r.Read()) list.Add(JsonSerializer.Deserialize(r.GetString(0), ApiJson.Default.Mutation)!);
            return list;
        }
    }

    /// <summary>
    /// Clears pushed mutations. An item edited again while the push was in flight keeps
    /// its newer outbox entry, since that has a different mutation id.
    /// </summary>
    public void CompletePush(IEnumerable<MutationResult> results)
    {
        lock (_lock)
        {
            using var tx = Begin();
            foreach (var res in results)
            {
                Exec("DELETE FROM outbox WHERE item_id = $id AND mutation_id = $m", ("$id", res.ItemId), ("$m", res.MutationId));
                if (res.Status == "applied")
                    Exec("UPDATE items SET rev = max(rev, $rev) WHERE id = $id", ("$rev", res.Rev), ("$id", res.ItemId));
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Applies pulled items and moves the sync position forward, in one transaction.
    /// A local edit that is still waiting to be pushed and is newer than the incoming
    /// version is kept: the server will take it on the next push (last writer wins).
    /// </summary>
    public void ApplyRemote(IEnumerable<Item> items, long nextRev)
    {
        var changed = false;
        lock (_lock)
        {
            using var tx = Begin();
            foreach (var remote in items)
            {
                var pending = Scalar("SELECT 1 FROM outbox WHERE item_id = $id", ("$id", remote.Id)) is not null;
                var local = pending ? GetUnlocked(remote.Id) : null;
                if (local is not null && local.UpdatedAt > remote.UpdatedAt) continue;
                if (pending) Exec("DELETE FROM outbox WHERE item_id = $id", ("$id", remote.Id));
                Upsert(remote);
                changed = true;
            }
            Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('last_rev', $v)", ("$v", nextRev.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            tx.Commit();
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets everything, e.g. when signing in to a different account or server.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            using var tx = Begin();
            Exec("DELETE FROM items; DELETE FROM item_tags; DELETE FROM items_fts; DELETE FROM outbox; DELETE FROM meta;");
            tx.Commit();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // --- helpers ------------------------------------------------------------------

    private void Upsert(Item item)
    {
        Exec(
            """
            INSERT OR REPLACE INTO items (id, text, url, title, description, site_name, favicon_url, file_id, file_name,
              file_mime, file_size, pinned, archived, tags, created_at, updated_at, deleted_at, rev)
            VALUES ($id, $text, $url, $title, $description, $site, $favicon, $fid, $fname, $fmime, $fsize,
              $pinned, $archived, $tags, $created, $updated, $deleted, $rev)
            """,
            ("$id", item.Id),
            ("$text", item.Text),
            ("$url", item.Url),
            ("$title", item.Title),
            ("$description", item.Description),
            ("$site", item.SiteName),
            ("$favicon", item.FaviconUrl),
            ("$fid", item.File?.Id),
            ("$fname", item.File?.Name),
            ("$fmime", item.File?.Mime),
            ("$fsize", item.File?.Size),
            ("$pinned", item.Pinned ? 1 : 0),
            ("$archived", item.Archived ? 1 : 0),
            ("$tags", JsonSerializer.Serialize(item.Tags, ApiJson.Default.IReadOnlyListString)),
            ("$created", item.CreatedAt),
            ("$updated", item.UpdatedAt),
            ("$deleted", item.DeletedAt),
            ("$rev", item.Rev)
        );
        Exec("DELETE FROM item_tags WHERE item_id = $id", ("$id", item.Id));
        Exec("DELETE FROM items_fts WHERE item_id = $id", ("$id", item.Id));
        if (item.IsDeleted) return;
        foreach (var tag in item.Tags)
            Exec("INSERT OR IGNORE INTO item_tags (item_id, tag) VALUES ($id, $tag)", ("$id", item.Id), ("$tag", tag));
        Exec(
            "INSERT INTO items_fts (item_id, text, title, description, url, file_name) VALUES ($id, $text, $title, $d, $url, $f)",
            ("$id", item.Id),
            ("$text", item.Text),
            ("$title", item.Title ?? ""),
            ("$d", item.Description ?? ""),
            ("$url", item.Url ?? ""),
            ("$f", item.File?.Name ?? "")
        );
    }

    private static Item ReadItem(SqliteDataReader r)
    {
        string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new Item
        {
            Id = r.GetString(0),
            Text = r.GetString(1),
            Url = Str(2),
            Title = Str(3),
            Description = Str(4),
            SiteName = Str(5),
            FaviconUrl = Str(6),
            File = r.IsDBNull(7) ? null : new FileInfo(r.GetString(7), Str(8) ?? "file", Str(9) ?? "", r.IsDBNull(10) ? 0 : r.GetInt64(10)),
            Pinned = r.GetInt64(11) != 0,
            Archived = r.GetInt64(12) != 0,
            Tags = JsonSerializer.Deserialize(r.GetString(13), ApiJson.Default.IReadOnlyListString) ?? [],
            CreatedAt = r.GetInt64(14),
            UpdatedAt = r.GetInt64(15),
            DeletedAt = r.IsDBNull(16) ? null : r.GetInt64(16),
            Rev = r.GetInt64(17),
        };
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    /// <summary>Free text to an FTS5 prefix query, like the server's. Null if there's nothing to search for.</summary>
    internal static string? ToFtsQuery(string q)
    {
        var tokens = Word().Matches(q).Select(m => $"\"{m.Value}\"*").ToList();
        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    // Microsoft.Data.Sqlite wants every command inside a transaction to name it.
    private Transaction Begin()
    {
        _tx = _db.BeginTransaction();
        return new Transaction(this);
    }

    private readonly struct Transaction(LocalStore store) : IDisposable
    {
        public void Commit() => store._tx!.Commit();

        public void Dispose()
        {
            store._tx?.Dispose();
            store._tx = null;
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}
