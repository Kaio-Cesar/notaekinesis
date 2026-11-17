using Notaekinesis.Core;
using Notaekinesis.Core.Api;
using Notaekinesis.Core.Data;

namespace Notaekinesis.Core.Tests;

public sealed class LocalStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("notaekinesis-").FullName;
    private readonly LocalStore _store;

    public LocalStoreTests() => _store = new LocalStore(Path.Combine(_dir, "test.db"));

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static ItemDto Remote(Item item, long rev) =>
        new(item.Id, "note", item.Text, item.Url, item.Title, null, null, null, null, item.Pinned, item.Archived, item.Tags,
            item.CreatedAt, item.UpdatedAt, item.DeletedAt, rev);

    [Fact]
    public void Saving_queues_one_mutation_per_item()
    {
        var item = _store.Save(Item.New("first", tags: ["#Work", "work", " ideas "]));
        _store.Save(item with { Text = "second" });

        var pending = Assert.Single(_store.PendingMutations());
        Assert.Equal("second", pending.Item.Text);
        Assert.Equal(["work", "ideas"], pending.Item.Tags);
        Assert.Equal("note", pending.Item.Kind);
    }

    [Fact]
    public void Search_matches_word_prefixes_ignoring_accents_and_tags_filter()
    {
        _store.Save(Item.New("Café com pão", tags: ["food"]));
        _store.Save(Item.New("", url: "https://example.com/docs", tags: ["work"]));
        _store.Save(Item.New("unrelated"));

        Assert.Equal("Café com pão", Assert.Single(_store.Query(new ItemQuery("cafe pa"))).Text);
        Assert.Equal(ItemKind.Link, Assert.Single(_store.Query(new ItemQuery("exam"))).Kind);
        Assert.Single(_store.Query(new ItemQuery(Tags: ["work"])));
        Assert.Equal(3, _store.Query(new ItemQuery("  ")).Count);
        Assert.Equal([new TagCount("food", 1), new TagCount("work", 1)], _store.Tags());
    }

    [Fact]
    public void Pinned_first_and_deleted_or_archived_items_are_hidden()
    {
        Clock.Source = () => 1000;
        var old = _store.Save(Item.New("old") with { Pinned = true });
        Clock.Source = () => 2000;
        var archived = _store.Save(Item.New("archived") with { Archived = true });
        var gone = _store.Save(Item.New("gone"));
        _store.Save(Item.New("new"));
        _store.Delete(gone);
        Clock.Source = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.Equal(["old", "new"], _store.Query(new ItemQuery()).Select(i => i.Text));
        Assert.Equal("archived", Assert.Single(_store.Query(new ItemQuery(Archived: true))).Text);
        Assert.Empty(_store.Query(new ItemQuery("gone")));
        Assert.NotNull(_store.Get(gone.Id)!.DeletedAt);
        Assert.Equal(old.Id, _store.Query(new ItemQuery())[0].Id);
    }

    [Fact]
    public void An_edit_is_always_newer_than_the_version_it_came_from_even_with_a_slow_clock()
    {
        var remote = Item.New("from phone") with { UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000 };
        _store.ApplyRemote([Remote(remote, 5).ToItem()], 5);

        var edited = _store.Save(_store.Get(remote.Id)! with { Text = "edited here" });
        Assert.Equal(remote.UpdatedAt + 1, edited.UpdatedAt);
        Assert.Equal(5, edited.Rev);
    }

    [Fact]
    public void Pull_keeps_a_newer_pending_local_edit_and_replaces_an_older_one()
    {
        var item = _store.Save(Item.New("local"));

        // An older remote version doesn't overwrite the pending edit.
        _store.ApplyRemote([Remote(item with { Text = "older remote", UpdatedAt = item.UpdatedAt - 10 }, 3).ToItem()], 3);
        Assert.Equal("local", _store.Get(item.Id)!.Text);
        Assert.Equal(1, _store.PendingCount);
        Assert.Equal(3, _store.LastRev);

        // A newer one wins, and the pending edit is dropped.
        _store.ApplyRemote([Remote(item with { Text = "newer remote", UpdatedAt = item.UpdatedAt + 10 }, 4).ToItem()], 4);
        Assert.Equal("newer remote", _store.Get(item.Id)!.Text);
        Assert.Equal(0, _store.PendingCount);
    }

    [Fact]
    public void Completing_a_push_keeps_edits_made_while_it_was_in_flight()
    {
        var item = _store.Save(Item.New("v1"));
        var sent = _store.PendingMutations();
        _store.Save(item with { Text = "v2" }); // while the push is in flight

        _store.CompletePush([new MutationResult(sent[0].MutationId, item.Id, "applied", 7)]);

        Assert.Equal("v2", Assert.Single(_store.PendingMutations()).Item.Text);
        Assert.Equal(7, _store.Get(item.Id)!.Rev);
    }

    [Fact]
    public void Server_urls_are_normalized()
    {
        Assert.Equal("https://notes.lan", Settings.NormalizeServerUrl(" notes.lan/ "));
        Assert.Equal("http://localhost:3000", Settings.NormalizeServerUrl("http://localhost:3000"));
        Assert.Equal("https://lab.example/notes", Settings.NormalizeServerUrl("https://lab.example/notes/"));
        Assert.Null(Settings.NormalizeServerUrl("ftp://x"));
        Assert.Null(Settings.NormalizeServerUrl(""));
    }
}
