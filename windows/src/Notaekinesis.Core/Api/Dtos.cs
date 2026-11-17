using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notaekinesis.Core.Api;

// Wire types for the server API (see /api/docs). JSON names are camelCase.

public sealed record FileDto(string Id, string Name, string Mime, long Size);

/// <summary>An item as the server returns it.</summary>
public sealed record ItemDto(
    string Id,
    string Kind,
    string Text,
    string? Url,
    string? Title,
    string? Description,
    string? SiteName,
    string? FaviconUrl,
    FileDto? File,
    bool Pinned,
    bool Archived,
    IReadOnlyList<string> Tags,
    long CreatedAt,
    long UpdatedAt,
    long? DeletedAt,
    long Rev
)
{
    public Item ToItem() =>
        new()
        {
            Id = Id,
            Text = Text,
            Url = Url,
            Title = Title,
            Description = Description,
            SiteName = SiteName,
            FaviconUrl = FaviconUrl,
            File = File is null ? null : new FileInfo(File.Id, File.Name, File.Mime, File.Size),
            Pinned = Pinned,
            Archived = Archived,
            Tags = Tags,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = DeletedAt,
            Rev = Rev,
        };
}

/// <summary>A full item as a client writes it.</summary>
public sealed record ItemInput(
    string Id,
    string Kind,
    string Text,
    string? Url,
    string? Title,
    string? Description,
    bool Pinned,
    bool Archived,
    IReadOnlyList<string> Tags,
    long CreatedAt,
    long UpdatedAt,
    long? DeletedAt,
    string? FileId
)
{
    public static ItemInput From(Item item) =>
        new(
            item.Id,
            item.Kind.ToString().ToLowerInvariant(),
            item.Text,
            item.Url,
            item.Title,
            item.Description,
            item.Pinned,
            item.Archived,
            item.Tags,
            item.CreatedAt,
            item.UpdatedAt,
            item.DeletedAt,
            item.File?.Id
        );
}

public sealed record Mutation(string MutationId, ItemInput Item);

public sealed record PushBody(IReadOnlyList<Mutation> Mutations);

public sealed record MutationResult(string MutationId, string ItemId, string Status, long Rev);

public sealed record PushResponse(IReadOnlyList<MutationResult> Results, long Rev);

public sealed record PullResponse(IReadOnlyList<ItemDto> Items, long NextRev, bool HasMore);

public sealed record RevEvent(long Rev);

public sealed record User(string Id, string Name, string Email, string? Image);

public sealed record VerifyBody(string Token);

public sealed record SessionInfo(string Token);

public sealed record VerifyResponse(SessionInfo Session, User User);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    NumberHandling = JsonNumberHandling.Strict
)]
[JsonSerializable(typeof(ItemDto))]
[JsonSerializable(typeof(Mutation))]
[JsonSerializable(typeof(PushBody))]
[JsonSerializable(typeof(PushResponse))]
[JsonSerializable(typeof(PullResponse))]
[JsonSerializable(typeof(RevEvent))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(VerifyBody))]
[JsonSerializable(typeof(VerifyResponse))]
[JsonSerializable(typeof(FileDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(Settings))]
public sealed partial class ApiJson : JsonSerializerContext;
