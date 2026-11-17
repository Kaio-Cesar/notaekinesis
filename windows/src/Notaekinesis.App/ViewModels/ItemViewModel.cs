using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Notaekinesis.App.Services;
using Notaekinesis.Core;

namespace Notaekinesis.App.ViewModels;

/// <summary>What a card shows. Rebuilt when the item changes; only the image loads later.</summary>
public sealed partial class ItemViewModel : INotifyPropertyChanged
{
    public ItemViewModel(Item item) => Item = item;

    public Item Item { get; }
    public string Id => Item.Id;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Host
    {
        get
        {
            if (Item.Url is null) return "";
            return Uri.TryCreate(Item.Url, UriKind.Absolute, out var uri) ? uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host : Item.Url;
        }
    }

    public bool HasLink => Item.Url is not null;
    public string LinkTitle => Item.Title ?? Host;
    public bool HasDescription => !string.IsNullOrEmpty(Item.Description);
    public bool HasText => Item.Text.Length > 0;
    public bool HasTags => Item.Tags.Count > 0;
    public string TagsText => string.Join("  ", Item.Tags.Select(t => "#" + t));
    public bool IsPinned => Item.Pinned;

    public bool IsImage => Item.File?.Mime.StartsWith("image/", StringComparison.Ordinal) == true;
    public bool IsOtherFile => Item.File is not null && !IsImage;
    public string FileLabel =>
        Item.File is null ? "" : $"{Item.File.Name} · {(Item.File.Mime == "application/pdf" ? "PDF" : Item.File.Mime)} · {FormatSize(Item.File.Size)}";

    public string When => FormatAgo(Item.CreatedAt);
    public string WhenFull => DateTimeOffset.FromUnixTimeMilliseconds(Item.CreatedAt).ToLocalTime().ToString("f");

    public string PinLabel => Item.Pinned ? "Unpin" : "Pin";
    public string ArchiveLabel => Item.Archived ? "Unarchive" : "Archive";
    public string CopyLabel => Item.File is not null ? "Copy file link" : Item.Url is not null ? "Copy link" : "Copy text";

    private ImageSource? _image;

    public ImageSource? Image
    {
        get => _image;
        private set
        {
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    /// <summary>Loads the attached image from the cache, downloading it once. Call on the UI thread.</summary>
    internal async Task LoadImage(FileCache cache, Core.Api.ApiClient? api)
    {
        if (!IsImage || _image is not null) return;
        var path = await cache.Get(api, Item.File!);
        if (path is null) return;
        // Decode at card width rather than full size: a phone photo would otherwise cost ~50 MB.
        Image = new BitmapImage { UriSource = new Uri(path), DecodePixelWidth = 720, DecodePixelType = DecodePixelType.Logical };
    }

    public void ReuseImageFrom(ItemViewModel? previous)
    {
        if (previous?.Item.File?.Id == Item.File?.Id) _image = previous?._image;
    }

    /// <summary>Same content as another view model, so the card can be left alone.</summary>
    public bool SameAs(ItemViewModel other) => other.Item.UpdatedAt == Item.UpdatedAt && other.Item.Rev == Item.Rev && other.When == When;

    internal static string FormatAgo(long epochMs)
    {
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} d ago";
        var date = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime();
        return date.Year == DateTime.Now.Year ? date.ToString("MMM d") : date.ToString("MMM d, yyyy");
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB"];
        double value = bytes / 1024.0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value < 10 ? $"{value:0.0} {units[unit]}" : $"{Math.Round(value)} {units[unit]}";
    }
}
