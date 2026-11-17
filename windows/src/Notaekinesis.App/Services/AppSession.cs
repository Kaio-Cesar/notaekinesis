using Notaekinesis.Core;
using Notaekinesis.Core.Api;
using Notaekinesis.Core.Auth;
using Notaekinesis.Core.Data;
using Notaekinesis.Core.Sync;

namespace Notaekinesis.App.Services;

/// <summary>
/// Everything that lives as long as the app: settings, the local database and, once
/// signed in, the API client and sync engine. The local database survives sign-out so
/// nothing unsynced is lost; it is cleared only when a different account signs in.
/// </summary>
internal sealed class AppSession : IAsyncDisposable
{
    public string Directory { get; }
    public Settings Settings { get; private set; }
    public LocalStore Store { get; }
    public ApiClient? Api { get; private set; }
    public SyncEngine? Sync { get; private set; }

    /// <summary>Raised when sync status or sign-in state changes. May fire on any thread.</summary>
    public event EventHandler? StateChanged;

    public bool IsSignedIn => Sync is not null;

    public AppSession(string? directory = null)
    {
        Directory = directory ?? Settings.DefaultDirectory;
        System.IO.Directory.CreateDirectory(Directory);
        Settings = Settings.Load(Directory);
        Store = new LocalStore(Path.Combine(Directory, "notaekinesis.db"));
    }

    /// <summary>Resumes the saved session, if there is one. Development builds can pass a token through the environment.</summary>
    public bool TryResume()
    {
        var devServer = Environment.GetEnvironmentVariable("NOTAEKINESIS_DEV_SERVER");
        var devToken = Environment.GetEnvironmentVariable("NOTAEKINESIS_DEV_TOKEN");
        if (devServer is not null && devToken is not null)
        {
            Settings = Settings with { ServerUrl = Settings.NormalizeServerUrl(devServer) };
            Start(devToken);
            return true;
        }
        if (Settings.ServerUrl is null || Settings.User is null) return false;
        var token = TokenStore.Read(Settings.ServerUrl);
        if (token is null) return false;
        Start(token);
        return true;
    }

    public async Task SignIn(string serverUrl, CancellationToken ct)
    {
        var result = await LoopbackLogin.SignIn(
            serverUrl,
            uri => _ = Windows.System.Launcher.LaunchUriAsync(uri),
            ct
        );
        // A different account (or server) shouldn't see the previous one's notes.
        if (Settings.ServerUrl != serverUrl || Settings.User?.Id != result.User.Id) Store.Reset();
        TokenStore.Write(serverUrl, result.User.Email, result.Token);
        Settings = Settings with { ServerUrl = serverUrl, User = result.User };
        Settings.Save(Directory);
        await StopSync();
        Start(result.Token);
    }

    public async Task SignOut()
    {
        if (Api is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Api.SignOut(cts.Token);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
        }
        if (Settings.ServerUrl is not null) TokenStore.Delete(Settings.ServerUrl);
        await StopSync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Start(string token)
    {
        Api = new ApiClient(Settings.ServerUrl!, token);
        Sync = new SyncEngine(Store, Api);
        Sync.StatusChanged += OnSyncStatus;
        Sync.Start();
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = RefreshUser(Api);
    }

    /// <summary>Picks up a new name or picture from the identity provider.</summary>
    private async Task RefreshUser(ApiClient api)
    {
        try
        {
            var user = await api.Me();
            if (user == Settings.User) return;
            Settings = Settings with { User = user };
            Settings.Save(Directory);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or UnauthorizedException or IOException) { }
    }

    private void OnSyncStatus(object? sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

    private async Task StopSync()
    {
        if (Sync is not null)
        {
            Sync.StatusChanged -= OnSyncStatus;
            await Sync.DisposeAsync();
            Sync = null;
        }
        Api?.Dispose();
        Api = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopSync();
        Store.Dispose();
    }
}
