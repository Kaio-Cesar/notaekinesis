using System.Text.Json;
using Notaekinesis.Core.Api;

namespace Notaekinesis.Core;

/// <summary>Per-user settings, kept as JSON next to the database. The session token is not stored here.</summary>
public sealed record Settings
{
    public string? ServerUrl { get; init; }
    public User? User { get; init; }

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notaekinesis");

    public static Settings Load(string directory)
    {
        try
        {
            var json = System.IO.File.ReadAllText(Path.Combine(directory, "settings.json"));
            return JsonSerializer.Deserialize(json, ApiJson.Default.Settings) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        System.IO.File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, ApiJson.Default.Settings));
        System.IO.File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Accepts "notes.lan" or "https://notes.lan/" and returns "https://notes.lan", or null if it isn't a URL.</summary>
    public static string? NormalizeServerUrl(string input)
    {
        var raw = input.Trim();
        if (raw.Length == 0) return null;
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return null;
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }
}
