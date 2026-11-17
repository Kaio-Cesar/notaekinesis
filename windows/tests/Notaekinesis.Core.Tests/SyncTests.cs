using System.Net;
using System.Text;
using System.Text.Json;
using Notaekinesis.Core;
using Notaekinesis.Core.Api;
using Notaekinesis.Core.Data;
using Notaekinesis.Core.Sync;

namespace Notaekinesis.Core.Tests;

/// <summary>A tiny stand-in for the server's sync endpoints, with last-writer-wins like the real one.</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public readonly Dictionary<string, ItemDto> Items = [];
    public long Rev;
    public int Pushes;
    public string? LastAuth;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastAuth = request.Headers.Authorization?.ToString();
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/sync/push")
        {
            Pushes++;
            var body = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(ct), ApiJson.Default.PushBody)!;
            var results = new List<MutationResult>();
            foreach (var m in body.Mutations)
            {
                if (Items.TryGetValue(m.Item.Id, out var cur) && cur.UpdatedAt >= m.Item.UpdatedAt)
                {
                    results.Add(new MutationResult(m.MutationId, m.Item.Id, "stale", cur.Rev));
                    continue;
                }
                var i = m.Item;
                Items[i.Id] = new ItemDto(i.Id, i.Kind, i.Text, i.Url, i.Title, i.Description, null, null, null, i.Pinned,
                    i.Archived, i.Tags, i.CreatedAt, i.UpdatedAt, i.DeletedAt, ++Rev);
                results.Add(new MutationResult(m.MutationId, i.Id, "applied", Rev));
            }
            return Json(JsonSerializer.Serialize(new PushResponse(results, Rev), ApiJson.Default.PushResponse));
        }
        if (path == "/api/sync/pull")
        {
            var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var since = long.Parse(q["since"]!);
            var limit = int.Parse(q["limit"]!);
            var page = Items.Values.Where(i => i.Rev > since).OrderBy(i => i.Rev).Take(limit + 1).ToList();
            var more = page.Count > limit;
            if (more) page.RemoveAt(page.Count - 1);
            var next = page.Count > 0 ? page[^1].Rev : Math.Max(since, Rev);
            return Json(JsonSerializer.Serialize(new PullResponse(page, next, more), ApiJson.Default.PullResponse));
        }
        if (path == "/api/sync/events")
        {
            var stream = "retry: 5000\nevent: rev\ndata: {\"rev\":1}\n\n: ping\n\nevent: rev\ndata: {\"rev\":2}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class SyncTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("notaekinesis-").FullName;
    private readonly FakeServer _server = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private (LocalStore, SyncEngine) Device(string name)
    {
        var store = new LocalStore(Path.Combine(_dir, name + ".db"));
        var api = new ApiClient("http://server.test", "token-" + name, _server);
        return (store, new SyncEngine(store, api));
    }

    [Fact]
    public async Task Two_devices_converge_and_deletes_propagate()
    {
        var (laptop, laptopSync) = Device("laptop");
        var (desktop, desktopSync) = Device("desktop");
        using (laptop)
        using (desktop)
        {
            var note = laptop.Save(Item.New("buy milk", tags: ["home"]));
            laptop.Save(Item.New("", url: "https://example.com"));
            await laptopSync.SyncOnce(TestContext.Current.CancellationToken);
            Assert.Equal(0, laptop.PendingCount);
            Assert.Equal("Bearer token-laptop", _server.LastAuth);

            await desktopSync.SyncOnce(TestContext.Current.CancellationToken);
            Assert.Equal(2, desktop.Query(new ItemQuery()).Count);

            desktop.Delete(desktop.Get(note.Id)!);
            await desktopSync.SyncOnce(TestContext.Current.CancellationToken);
            await laptopSync.SyncOnce(TestContext.Current.CancellationToken);

            Assert.Single(laptop.Query(new ItemQuery()));
            Assert.Equal(_server.Rev, laptop.LastRev);
        }
    }

    [Fact]
    public async Task Offline_edits_on_both_sides_resolve_to_the_newest()
    {
        var (a, aSync) = Device("a");
        var (b, bSync) = Device("b");
        using (a)
        using (b)
        {
            var item = a.Save(Item.New("original"));
            await aSync.SyncOnce(TestContext.Current.CancellationToken);
            await bSync.SyncOnce(TestContext.Current.CancellationToken);

            var t = item.UpdatedAt;
            Clock.Source = () => t + 100;
            a.Save(a.Get(item.Id)! with { Text = "edit on a" });
            Clock.Source = () => t + 200;
            b.Save(b.Get(item.Id)! with { Text = "edit on b (later)" });
            Clock.Source = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await bSync.SyncOnce(TestContext.Current.CancellationToken);
            await aSync.SyncOnce(TestContext.Current.CancellationToken); // a's push is stale; the pull brings b's
            await bSync.SyncOnce(TestContext.Current.CancellationToken);

            Assert.Equal("edit on b (later)", a.Get(item.Id)!.Text);
            Assert.Equal("edit on b (later)", b.Get(item.Id)!.Text);
            Assert.Equal(0, a.PendingCount);
        }
    }

    [Fact]
    public async Task Pull_pages_through_everything()
    {
        for (var n = 0; n < 1203; n++)
        {
            var id = Guid.CreateVersion7().ToString();
            _server.Items[id] = new ItemDto(id, "note", $"n{n}", null, null, null, null, null, null, false, false, [], n, n, null, ++_server.Rev);
        }
        var (store, sync) = Device("big");
        using (store)
        {
            await sync.SyncOnce(TestContext.Current.CancellationToken);
            Assert.Equal(1203, store.Query(new ItemQuery(Limit: 5000)).Count);
            Assert.Equal(1203, store.LastRev);
        }
    }

    [Fact]
    public async Task Event_stream_yields_revisions_and_skips_heartbeats()
    {
        using var api = new ApiClient("http://server.test", "t", _server);
        var revs = new List<long>();
        await foreach (var rev in api.Events(TestContext.Current.CancellationToken)) revs.Add(rev);
        Assert.Equal([1L, 2L], revs);
    }

    [Fact]
    public async Task A_revoked_session_reports_signed_out()
    {
        using var api = new ApiClient("http://server.test", "t", _server);
        await Assert.ThrowsAsync<UnauthorizedException>(() => api.Me(TestContext.Current.CancellationToken));
    }
}
