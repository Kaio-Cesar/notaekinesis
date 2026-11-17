namespace Notaekinesis.Core;

public enum ItemKind
{
    Note,
    Link,
    File,
}

public sealed record FileInfo(string Id, string Name, string Mime, long Size);

/// <summary>A note, link or file, as stored locally. Times are Unix epoch milliseconds.</summary>
public sealed record Item
{
    public required string Id { get; init; }
    public ItemKind Kind => File is not null ? ItemKind.File : Url is not null ? ItemKind.Link : ItemKind.Note;

    /// <summary>Markdown. For links and files it's the optional text that goes with them.</summary>
    public string Text { get; init; } = "";
    public string? Url { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? SiteName { get; init; }
    public string? FaviconUrl { get; init; }
    public FileInfo? File { get; init; }
    public bool Pinned { get; init; }
    public bool Archived { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
    public long? DeletedAt { get; init; }

    /// <summary>The server revision this version came from; 0 if it has never been synced.</summary>
    public long Rev { get; init; }

    public bool IsDeleted => DeletedAt is not null;

    public static Item New(string text = "", string? url = null, IEnumerable<string>? tags = null)
    {
        var now = Clock.Now();
        return new Item
        {
            Id = Guid.CreateVersion7().ToString(),
            Text = text,
            Url = url,
            Tags = Tag.Normalize(tags ?? []),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}

public sealed record TagCount(string Tag, int Count);

public sealed record ItemQuery(string? Search = null, IReadOnlyList<string>? Tags = null, bool Archived = false, int Limit = 1000);

public static class Tag
{
    /// <summary>Same rules as the server: trimmed, no leading '#', lower case, no duplicates.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> tags)
    {
        var seen = new List<string>();
        foreach (var raw in tags)
        {
            var tag = raw.Trim().TrimStart('#').Trim().ToLowerInvariant();
            if (tag.Length > 0 && !seen.Contains(tag)) seen.Add(tag);
        }
        return seen;
    }
}

public static class Clock
{
    /// <summary>Swappable in tests.</summary>
    internal static Func<long> Source { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long Now() => Source();
}
