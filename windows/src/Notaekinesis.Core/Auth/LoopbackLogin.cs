using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Notaekinesis.Core.Api;

namespace Notaekinesis.Core.Auth;

public sealed record LoginResult(string Token, User User);

/// <summary>
/// Signs in through the server's /native-login page in the system browser (RFC 8252):
/// the server runs the OIDC login, then redirects to a one-shot listener on 127.0.0.1
/// with a one-time token, which is traded for a session token.
/// </summary>
public static class LoopbackLogin
{
    /// <param name="openBrowser">Opens the URL in the user's browser.</param>
    public static async Task<LoginResult> SignIn(string serverUrl, Action<Uri> openBrowser, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var state = Base64Url(RandomNumberGenerator.GetBytes(24));
            var redirect = $"http://127.0.0.1:{port}/callback";
            var start = new Uri(
                $"{serverUrl.TrimEnd('/')}/native-login?redirect={Uri.EscapeDataString(redirect)}&state={Uri.EscapeDataString(state)}"
            );
            openBrowser(start);

            var oneTimeToken = await WaitForCallback(listener, state, ct).ConfigureAwait(false);
            using var api = new ApiClient(serverUrl, token: null);
            var verified = await api.VerifyOneTimeToken(oneTimeToken, ct).ConfigureAwait(false);
            return new LoginResult(verified.Session.Token, verified.User);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> WaitForCallback(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            string? requestLine;
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                requestLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;
            }

            // "GET /callback?token=…&state=… HTTP/1.1"
            var parts = requestLine?.Split(' ');
            var target = parts is { Length: 3 } && parts[0] == "GET" ? parts[1] : null;
            if (target is null || !target.StartsWith("/callback?", StringComparison.Ordinal))
            {
                await Respond(stream, "404 Not Found", "Not found.", ct).ConfigureAwait(false);
                continue; // e.g. /favicon.ico
            }

            var query = HttpUtility.ParseQueryString(target["/callback?".Length..]);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(query["state"] ?? ""), Encoding.UTF8.GetBytes(state)))
            {
                await Respond(stream, "400 Bad Request", "This sign-in link doesn't match. Start again from the app.", ct).ConfigureAwait(false);
                continue;
            }
            var token = query["token"];
            if (string.IsNullOrEmpty(token))
            {
                await Respond(stream, "400 Bad Request", "Sign-in failed.", ct).ConfigureAwait(false);
                throw new InvalidOperationException("The server didn't send a token.");
            }
            await Respond(stream, "200 OK", "You're signed in. You can close this tab and go back to notaekinesis.", ct).ConfigureAwait(false);
            return token;
        }
    }

    private static async Task Respond(NetworkStream stream, string status, string message, CancellationToken ct)
    {
        var html =
            "<!doctype html><meta charset=utf-8><title>notaekinesis</title>"
            + "<body style=\"font:16px system-ui;display:grid;place-items:center;height:90vh;color-scheme:light dark\">"
            + $"<p>{WebUtility.HtmlEncode(message)}</p>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n"
                + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n"
        );
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
