using System.Net;
using System.Text;
using Notaekinesis.Core.Api;

namespace Notaekinesis.Core.Tests;

public sealed class ApiClientTests
{
    [Fact]
    public async Task Uploads_look_like_a_browsers_so_the_server_reads_a_file()
    {
        using var form = ApiClient.FilePart(new MemoryStream([1, 2, 3]), "Screen \"shot\" ç.png", null);
        var body = Encoding.UTF8.GetString(await form.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // Quoted name and raw UTF-8 file name (not MIME-encoded), plus a part Content-Type:
        // without these Bun parses the part as a text field and the server answers "Expected kind 'File'".
        Assert.Contains("Content-Disposition: form-data; name=\"file\"; filename=\"Screen %22shot%22 ç.png\"\r\n", body, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/octet-stream\r\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("filename*", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_errors_become_short_readable_messages()
    {
        var validation = """{"type":"validation","on":"body","summary":"Expected kind 'File'","errors":[{"summary":"x"}]}""";
        Assert.Equal("Expected kind 'File'", ApiClient.ErrorMessage(validation, HttpStatusCode.UnprocessableEntity));
        Assert.Equal("Files can be at most 100 MB", ApiClient.ErrorMessage("Files can be at most 100 MB", HttpStatusCode.RequestEntityTooLarge));
        Assert.Equal("The server answered 502 BadGateway.", ApiClient.ErrorMessage("<html>proxy error</html>", HttpStatusCode.BadGateway));
        Assert.Equal("The server answered 500 InternalServerError.", ApiClient.ErrorMessage("", HttpStatusCode.InternalServerError));
        Assert.EndsWith("…", ApiClient.ErrorMessage(new string('x', 500), HttpStatusCode.BadRequest), StringComparison.Ordinal);
    }
}
