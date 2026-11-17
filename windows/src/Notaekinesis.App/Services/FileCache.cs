using System.Collections.Concurrent;
using Notaekinesis.Core.Api;

namespace Notaekinesis.App.Services;

/// <summary>Downloaded attachments, kept on disk. Files never change on the server, so a cached copy is always good.</summary>
internal sealed class FileCache(string directory)
{
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();

    /// <summary>Kept under its own name (in a folder per file) so "open" picks the right app.</summary>
    public string PathOf(Core.FileInfo file)
    {
        var name = string.Concat(file.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return Path.Combine(directory, file.Id, name.Length > 0 ? name : "file");
    }

    /// <summary>The local path of the file, downloading it first if needed. Null if it can't be fetched right now.</summary>
    public Task<string?> Get(ApiClient? api, Core.FileInfo file)
    {
        var path = PathOf(file);
        if (File.Exists(path)) return Task.FromResult<string?>(path);
        if (api is null) return Task.FromResult<string?>(null);
        return _inflight.GetOrAdd(file.Id, _ => Download(api, file.Id, path));
    }

    private async Task<string?> Download(ApiClient api, string fileId, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".part";
            await using (var source = await api.Download(fileId))
            await using (var target = File.Create(tmp))
                await source.CopyToAsync(target);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _inflight.TryRemove(fileId, out _);
        }
    }
}
