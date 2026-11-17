using Notaekinesis.Core.Api;
using Notaekinesis.Core.Data;

namespace Notaekinesis.Core.Sync;

public enum SyncStatus
{
    Idle,
    Syncing,
    Offline,
    SignedOut,
}

/// <summary>
/// Keeps the local store and the server in step. There is no polling: it syncs when
/// something is saved locally, when the server's event stream reports a new revision,
/// and whenever the event stream (re)connects. While offline it retries with backoff.
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    private readonly LocalStore _store;
    private readonly ApiClient _api;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private int _requested;
    private Task? _worker;
    private Task? _listener;

    public SyncStatus Status { get; private set; } = SyncStatus.Idle;
    public Exception? LastError { get; private set; }

    /// <summary>May fire on any thread.</summary>
    public event EventHandler? StatusChanged;

    public SyncEngine(LocalStore store, ApiClient api)
    {
        _store = store;
        _api = api;
    }

    public void Start()
    {
        _store.LocalWrite += OnLocalWrite;
        _worker = Task.Run(() => Worker(_stop.Token));
        _listener = Task.Run(() => Listen(_stop.Token));
        RequestSync();
    }

    public async ValueTask DisposeAsync()
    {
        _store.LocalWrite -= OnLocalWrite;
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_worker ?? Task.CompletedTask, _listener ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _wake.Dispose();
    }

    private void OnLocalWrite(object? sender, EventArgs e) => RequestSync();

    /// <summary>Asks for a sync soon. Requests made while one is running cause one more run after it.</summary>
    public void RequestSync()
    {
        if (Interlocked.Exchange(ref _requested, 1) == 0) _wake.Release();
    }

    private void SetStatus(SyncStatus status, Exception? error = null)
    {
        LastError = error;
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task Worker(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            await _wake.WaitAsync(ct).ConfigureAwait(false);
            // Let a burst of edits (typing, a multi-select) settle into one push.
            await Task.Delay(150, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _requested, 0);
            try
            {
                SetStatus(SyncStatus.Syncing);
                await SyncOnce(ct).ConfigureAwait(false);
                SetStatus(SyncStatus.Idle);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (UnauthorizedException e)
            {
                SetStatus(SyncStatus.SignedOut, e);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                SetStatus(SyncStatus.Offline, e);
                _ = RetryLater(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));
            }
        }
    }

    private async Task RetryLater(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            RequestSync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Push everything pending, then pull everything new.</summary>
    internal async Task SyncOnce(CancellationToken ct)
    {
        while (true)
        {
            var batch = _store.PendingMutations();
            if (batch.Count == 0) break;
            var res = await _api.Push(batch, ct).ConfigureAwait(false);
            _store.CompletePush(res.Results);
            if (batch.Count < 500) break;
        }

        var since = _store.LastRev;
        while (true)
        {
            var page = await _api.Pull(since, 500, ct).ConfigureAwait(false);
            _store.ApplyRemote(page.Items.Select(i => i.ToItem()), page.NextRev);
            since = page.NextRev;
            if (!page.HasMore) break;
        }
    }

    private async Task Listen(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var rev in _api.Events(ct).ConfigureAwait(false))
                {
                    backoff = TimeSpan.FromSeconds(2);
                    // The first event comes right after connecting, so a reconnect always catches up.
                    if (rev > _store.LastRev || _store.PendingCount > 0) RequestSync();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (UnauthorizedException e)
            {
                SetStatus(SyncStatus.SignedOut, e);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
                SetStatus(SyncStatus.Offline, e);
            }
            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 120));
        }
    }
}
