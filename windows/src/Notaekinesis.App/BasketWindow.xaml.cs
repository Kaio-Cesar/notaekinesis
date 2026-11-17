using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Notaekinesis.App.Services;
using Notaekinesis.Core;
using Notaekinesis.Core.Api;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Notaekinesis.App;

/// <summary>
/// The quick-save popup. It opens near the pointer on the hotkey and stays up while you
/// switch to another app to drag something in. Whatever lands on it is saved at once;
/// a note or #tags typed afterwards are added to that item. Esc or the hotkey closes it.
/// </summary>
public sealed partial class BasketWindow : Window
{
    private const int WidthDip = 360;
    private const int HeightDip = 210;

    private readonly AppSession _session;
    private readonly DispatcherQueueTimer _hideTimer;
    private string? _savedId;
    private bool _pointerInside;
    private bool _busy;
    private bool _closing;

    internal BasketWindow(AppSession session)
    {
        _session = session;
        InitializeComponent();

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "notaekinesis quick add";
        Native.RoundCorners(WinRT.Interop.WindowNative.GetWindowHandle(this));
        // Alt+F4 hides it like Esc; the app keeps one basket for its whole life.
        AppWindow.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            Hide();
        };

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            // Not while the user is about to add something to what was just saved.
            if (_pointerInside || NoteBox.Text.Length > 0) return;
            Hide();
        };
    }

    public bool IsShown => AppWindow.IsVisible;

    /// <summary>Really closes it, when the app quits.</summary>
    internal void Shutdown()
    {
        _closing = true;
        Close();
    }

    public void Toggle()
    {
        if (IsShown) Hide();
        else ShowNearPointer();
    }

    public void ShowNearPointer()
    {
        Native.GetCursorPos(out var cursor);
        var scale = Native.ScaleAt(cursor);
        int w = (int)(WidthDip * scale), h = (int)(HeightDip * scale), gap = (int)(16 * scale);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        var x = Math.Clamp(cursor.X - w / 2, area.X + gap, area.X + area.Width - w - gap);
        // Below the pointer, or above it when there's no room.
        var y = cursor.Y + gap + h <= area.Y + area.Height ? cursor.Y + gap : cursor.Y - gap - h;
        y = Math.Clamp(y, area.Y + gap, area.Y + area.Height - h - gap);
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));

        Reset();
        AppWindow.Show();
        Activate();
        // The hotkey message gives this process the right to take the foreground.
        Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        NoteBox.Focus(FocusState.Programmatic);
    }

    public void Hide()
    {
        _hideTimer.Stop();
        AppWindow.Hide();
        Reset();
    }

    private void Reset()
    {
        _savedId = null;
        _busy = false;
        NoteBox.Text = "";
        NoteBox.PlaceholderText = "Quick note… #tags";
        Show(IdlePanel);
        IdleHint.Text = _session.IsSignedIn ? "or paste with Ctrl+V · Esc to close" : "Not signed in: notes stay on this PC until you sign in";
    }

    private void Show(UIElement panel)
    {
        IdlePanel.Visibility = panel == IdlePanel ? Visibility.Visible : Visibility.Collapsed;
        BusyPanel.Visibility = panel == BusyPanel ? Visibility.Visible : Visibility.Collapsed;
        SavedPanel.Visibility = panel == SavedPanel ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = panel == ErrorPanel ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = panel == BusyPanel;
    }

    private void ShowSaved(Item item, TimeSpan hideAfter)
    {
        _savedId = item.Id;
        SavedTitle.Text = item.File?.Name ?? item.Title ?? item.Url ?? FirstLine(item.Text);
        SavedDetail.Text = item.Url is not null && item.Title is not null ? Host(item.Url)
            : item.File is not null ? ViewModels.ItemViewModel.FormatSize(item.File.Size)
            : "";
        NoteBox.PlaceholderText = "Add a note or #tags to it…";
        Show(SavedPanel);
        _hideTimer.Interval = hideAfter;
        _hideTimer.Start();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        Show(ErrorPanel);
        _hideTimer.Stop();
    }

    // --- typing -----------------------------------------------------------------------

    private void OnNoteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var capture = CaptureParser.Parse(NoteBox.Text);
        if (capture is null) return;

        if (_savedId is not null && _session.Store.Get(_savedId) is { } saved)
        {
            // Add to what was just dropped: the text goes below any existing text, tags are merged.
            var text = string.IsNullOrEmpty(saved.Text) ? capture.Text : capture.Text.Length == 0 ? saved.Text : $"{saved.Text}\n\n{capture.Text}";
            var url = saved.Url ?? (saved.File is null ? capture.Url : null);
            _session.Store.Save(saved with { Text = text, Url = url, Tags = [.. saved.Tags, .. capture.Tags] });
            Hide();
            return;
        }

        var item = _session.Store.Save(Item.New(capture.Text, capture.Url, capture.Tags));
        NoteBox.Text = "";
        ShowSaved(item, TimeSpan.FromSeconds(1.2));
    }

    private void OnNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (NoteBox.Text.Length > 0) _hideTimer.Stop();
        else if (_savedId is not null) _hideTimer.Start();
    }

    private void OnNoteFocus(object sender, RoutedEventArgs e)
    {
        if (ErrorPanel.Visibility == Visibility.Visible) Show(IdlePanel);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        Hide();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _pointerInside = true;

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        if (_savedId is not null && NoteBox.Text.Length == 0) _hideTimer.Start();
    }

    /// <summary>Files and images on the clipboard are saved directly; text is pasted into the box as usual.</summary>
    private async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        var data = Clipboard.GetContent();
        if (!data.Contains(StandardDataFormats.StorageItems) && !data.Contains(StandardDataFormats.Bitmap)) return;
        e.Handled = true;
        await Take(data);
    }

    // --- drag and drop ------------------------------------------------------------------

    private static bool Useful(DataPackageView data) =>
        data.Contains(StandardDataFormats.StorageItems)
        || data.Contains(StandardDataFormats.WebLink)
        || data.Contains(StandardDataFormats.Uri)
        || data.Contains(StandardDataFormats.Text)
        || data.Contains(StandardDataFormats.Bitmap);

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!Useful(e.DataView) || _busy) return;
        DropHighlight.Opacity = 0.15;
        _hideTimer.Stop();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!Useful(e.DataView) || _busy) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Save to notaekinesis";
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHighlight.Opacity = 0;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Opacity = 0;
        var deferral = e.GetDeferral();
        try
        {
            await Take(e.DataView);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Saves whatever a drop or paste carries: files first, then an image, then a link or text.</summary>
    private async Task Take(DataPackageView data)
    {
        if (_busy) return;
        try
        {
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var files = (await data.GetStorageItemsAsync()).OfType<IStorageFile>().ToList();
                var supported = files.Where(f => DropReader.IsSupportedFile(f.Name)).ToList();
                if (supported.Count > 0)
                {
                    await SaveFiles(supported, skipped: files.Count - supported.Count);
                    return;
                }
                // A link dragged from a browser also carries a .url shortcut "file": save the link instead.
                var hasLink = data.Contains(StandardDataFormats.WebLink) || data.Contains(StandardDataFormats.Uri) || data.Contains(StandardDataFormats.Text);
                if (!hasLink && !data.Contains(StandardDataFormats.Bitmap))
                {
                    ShowError(files.Count == 0 ? "Nothing to save here." : "Only images, videos and PDFs can be saved.");
                    return;
                }
            }
            if (data.Contains(StandardDataFormats.Bitmap))
            {
                await SaveBitmap(await data.GetBitmapAsync());
                return;
            }

            string? link = null;
            if (data.Contains(StandardDataFormats.WebLink)) link = (await data.GetWebLinkAsync()).ToString();
            else if (data.Contains(StandardDataFormats.Uri)) link = (await data.GetUriAsync()).ToString();
            string? html = null;
            if (link is not null && data.Contains(StandardDataFormats.Html))
                html = HtmlFormatHelper.GetStaticFragment(await data.GetHtmlFormatAsync());
            var text = data.Contains(StandardDataFormats.Text) ? await data.GetTextAsync() : null;

            var capture = DropReader.Read(link, html, text);
            if (capture is null) return;
            var item = _session.Store.Save(Item.New(capture.Text, capture.Url) with { Title = capture.Title });
            ShowSaved(item, TimeSpan.FromSeconds(2.5));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowError($"Couldn't read what was dropped: {ex.Message}");
        }
    }

    /// <summary>
    /// Uploads files one after another. They're read through the storage API rather than by path,
    /// so files that only exist inside the drag (an image dragged out of a browser) work too.
    /// </summary>
    private async Task SaveFiles(List<IStorageFile> supported, int skipped)
    {
        Item? last = null;
        for (var i = 0; i < supported.Count; i++)
        {
            SetBusy(supported.Count == 1 ? "Uploading…" : $"Uploading {i + 1} of {supported.Count}…");
            using var source = await supported[i].OpenReadAsync();
            await using var stream = source.AsStreamForRead();
            last = await Upload(stream, supported[i].Name);
            if (last is null) return;
        }
        ShowSaved(last!, TimeSpan.FromSeconds(2.5));
        if (supported.Count > 1) SavedTitle.Text = $"{supported.Count} files";
        if (skipped > 0) SavedDetail.Text = $"{skipped} skipped: only images, videos and PDFs are supported";
    }

    /// <summary>A copied image (e.g. a screenshot) arrives as a bitmap; it's saved as PNG.</summary>
    private async Task SaveBitmap(RandomAccessStreamReference reference)
    {
        SetBusy("Uploading image…");
        using var source = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var png = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        png.Seek(0);
        await using var stream = png.AsStreamForRead();
        var item = await Upload(stream, $"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        if (item is not null) ShowSaved(item, TimeSpan.FromSeconds(2.5));
    }

    private async Task<Item?> Upload(Stream content, string name)
    {
        if (!_session.IsSignedIn)
        {
            ShowError("Sign in to save files.");
            return null;
        }
        try
        {
            return await _session.SaveFile(content, name);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedException)
        {
            ShowError(
                ex is HttpRequestException { StatusCode: not null } http ? $"The server refused {name}: {http.Message}"
                : $"Couldn't upload {name}. Files need a connection to the server."
            );
            return null;
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetBusy(string message)
    {
        _busy = true;
        BusyText.Text = message;
        Show(BusyPanel);
        _hideTimer.Stop();
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 0 ? line : "Note";
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}
