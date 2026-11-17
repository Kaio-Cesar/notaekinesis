using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Notaekinesis.Core.Api;

/// <summary>The session is gone (expired or revoked); the user has to sign in again.</summary>
public sealed class UnauthorizedException() : Exception("Signed out");

/// <summary>Talks to the notaekinesis server with a bearer session token.</summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;

    public Uri BaseUrl { get; }

    public ApiClient(string serverUrl, string? token, HttpMessageHandler? handler = null)
    {
        BaseUrl = new Uri(serverUrl.TrimEnd('/') + "/");
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2) })
        {
            BaseAddress = BaseUrl,
            Timeout = Timeout.InfiniteTimeSpan, // per call below; the event stream stays open
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("notaekinesis-windows", "0.1"));
        if (token is not null) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose() => _http.Dispose();

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private async Task<T> Send<T>(HttpRequestMessage request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using var res = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedException();
        if (!res.IsSuccessStatusCode) throw await Failure(res, cts.Token).ConfigureAwait(false);
        return (await res.Content.ReadFromJsonAsync(type, cts.Token).ConfigureAwait(false))!;
    }

    /// <summary>
    /// An error with a message fit to show: the server answers with plain text ("Files can be at
    /// most 100 MB") or, for a validation failure, JSON whose "summary" says what was wrong.
    /// </summary>
    private static async Task<HttpRequestException> Failure(HttpResponseMessage res, CancellationToken ct)
    {
        var body = (await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        return new HttpRequestException(ErrorMessage(body, res.StatusCode), null, res.StatusCode);
    }

    internal static string ErrorMessage(string body, HttpStatusCode status)
    {
        if (body.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                foreach (var key in (string[])["summary", "message", "error"])
                    if (json.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text)
                        return text;
            }
            catch (JsonException) { }
            body = "";
        }
        if (body.Length == 0 || body.StartsWith('<')) return $"The server answered {(int)status} {status}.";
        return body.Length > 200 ? body[..200] + "…" : body;
    }

    public Task<User> Me(CancellationToken ct = default) =>
        Send(new HttpRequestMessage(HttpMethod.Get, "api/me"), ApiJson.Default.User, ct);

    /// <summary>Trades the one-time token from the native login page for a session.</summary>
    public Task<VerifyResponse> VerifyOneTimeToken(string token, CancellationToken ct = default) =>
        Send(
            new HttpRequestMessage(HttpMethod.Post, "api/auth/one-time-token/verify")
            {
                Content = JsonContent.Create(new VerifyBody(token), ApiJson.Default.VerifyBody),
            },
            ApiJson.Default.VerifyResponse,
            ct
        );

    public async Task SignOut(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/auth/sign-out") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    public Task<PushResponse> Push(IReadOnlyList<Mutation> mutations, CancellationToken ct = default) =>
        Send(
            new HttpRequestMessage(HttpMethod.Post, "api/sync/push")
            {
                Content = JsonContent.Create(new PushBody(mutations), ApiJson.Default.PushBody),
            },
            ApiJson.Default.PushResponse,
            ct
        );

    public Task<PullResponse> Pull(long since, int limit = 500, CancellationToken ct = default) =>
        Send(new HttpRequestMessage(HttpMethod.Get, $"api/sync/pull?since={since}&limit={limit}"), ApiJson.Default.PullResponse, ct);

    public async Task<FileDto> Upload(Stream content, string fileName, string? mime, CancellationToken ct = default)
    {
        using var form = FilePart(content, fileName, mime);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/files") { Content = form };
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedException();
        if (!res.IsSuccessStatusCode) throw await Failure(res, ct).ConfigureAwait(false);
        return (await res.Content.ReadFromJsonAsync(ApiJson.Default.FileDto, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// A multipart body shaped like a browser's: a quoted UTF-8 file name and a Content-Type on
    /// the part. .NET's default (unquoted name, MIME-encoded file name, no type) makes Bun read
    /// the part as a text field instead of a file.
    /// </summary>
    internal static MultipartFormDataContent FilePart(Stream content, string fileName, string? mime)
    {
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime ?? "application/octet-stream");
        // Like browsers: line breaks dropped, quotes percent-encoded.
        var safeName = new string([.. fileName.Where(c => c != (char)13 && c != (char)10)]).Replace("\"", "%22", StringComparison.Ordinal);
        file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{safeName}\"");
        var form = new MultipartFormDataContent { HeaderEncodingSelector = (_, _) => System.Text.Encoding.UTF8 };
        form.Add(file);
        return form;
    }

    /// <summary>Downloads a file's bytes. The caller disposes the stream.</summary>
    public async Task<Stream> Download(string fileId, CancellationToken ct = default)
    {
        var res = await _http.GetAsync($"api/files/{Uri.EscapeDataString(fileId)}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedException();
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The server's revision, every time it changes (Server-Sent Events). Ends when the
    /// connection drops; the server sends a heartbeat every 20 s, so a silent minute means it's gone.
    /// </summary>
    public async IAsyncEnumerable<long> Events([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/sync/events");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedException();
        res.EnsureSuccessStatusCode();
        using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? data = null;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(60));
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                yield break; // no heartbeat: treat as disconnected
            }
            if (line is null) yield break;
            if (line.StartsWith("data:", StringComparison.Ordinal)) data = line[5..].Trim();
            else if (line.Length == 0 && data is not null)
            {
                RevEvent? ev = null;
                try
                {
                    ev = JsonSerializer.Deserialize(data, ApiJson.Default.RevEvent);
                }
                catch (JsonException) { }
                data = null;
                if (ev is not null) yield return ev.Rev;
            }
        }
    }
}
