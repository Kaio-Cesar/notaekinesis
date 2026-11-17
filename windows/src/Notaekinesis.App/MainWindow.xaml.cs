using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Notaekinesis.App.Services;
using Notaekinesis.App.ViewModels;
using Notaekinesis.Core;
using Notaekinesis.Core.Sync;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Notaekinesis.App;

public sealed partial class MainWindow : Window
{
    private readonly AppSession _session;
    private readonly FileCache _files;
    private readonly ObservableCollection<ItemViewModel> _items = [];
    private readonly DispatcherQueueTimer _reloadTimer;
    private readonly DispatcherQueueTimer _undoTimer;
    private readonly List<string> _activeTags = [];
    private IReadOnlyList<TagCount> _shownTags = [];
    private Item? _lastDeleted;
    private CancellationTokenSource? _signIn;

    internal MainWindow(AppSession session)
    {
        _session = session;
        _files = new FileCache(Path.Combine(session.Directory, "files"));
        InitializeComponent();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var scale = Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(600 * scale), (int)(860 * scale)));

        Feed.ItemsSource = _items;

        // Store and sync events arrive on background threads; coalesce them into one UI refresh.
        _reloadTimer = DispatcherQueue.CreateTimer();
        _reloadTimer.Interval = TimeSpan.FromMilliseconds(40);
        _reloadTimer.IsRepeating = false;
        _reloadTimer.Tick += (_, _) => Reload();
        _undoTimer = DispatcherQueue.CreateTimer();
        _undoTimer.Interval = TimeSpan.FromSeconds(6);
        _undoTimer.IsRepeating = false;
        _undoTimer.Tick += (_, _) => UndoBar.IsOpen = false;

        _session.Store.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => _reloadTimer.Start());
        _session.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateState);
        Activated += (_, e) =>
        {
            // Relative times ("5 min ago") go stale while the window is in the background.
            if (e.WindowActivationState != WindowActivationState.Deactivated) _reloadTimer.Start();
        };

        UpdateState();
        Reload();
    }

    // --- signed in or not --------------------------------------------------------

    private void UpdateState()
    {
        if (_session.Sync?.Status == SyncStatus.SignedOut)
        {
            _ = _session.SignOut(); // raises StateChanged again
            ShowSignInError("Your session has ended. Sign in again to keep syncing.");
            return;
        }

        var signedIn = _session.IsSignedIn;
        // Notes saved before signing out are still here, so the feed stays usable offline.
        FeedPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignInPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = FeedPanel.Visibility;
        AccountButton.Visibility = FeedPanel.Visibility;
        SyncIcon.Visibility = FeedPanel.Visibility;

        if (!signedIn)
        {
            ServerBox.Text = _session.Settings.ServerUrl ?? "";
            return;
        }

        var user = _session.Settings.User;
        Avatar.DisplayName = user?.Name ?? "";
        if (user?.Image is { } image && Uri.TryCreate(image, UriKind.Absolute, out var imageUri))
            Avatar.ProfilePicture = new BitmapImage(imageUri) { DecodePixelWidth = 56 };
        AccountName.Text = user is null ? "Signed in" : $"{user.Name} ({user.Email})";
        ServerName.Text = _session.Settings.ServerUrl ?? "";

        var (glyph, tip) = _session.Sync!.Status switch
        {
            SyncStatus.Syncing => ("\uE895", "Syncing…"),
            SyncStatus.Offline => ("\uE871", "Offline. Changes are saved here and will sync when the server is reachable."),
            _ => ("\uE930", "Up to date"),
        };
        SyncIcon.Glyph = glyph;
        ToolTipService.SetToolTip(SyncIcon, tip);
    }

    private void ShowSignInError(string? message)
    {
        SignInError.Message = message ?? "";
        SignInError.IsOpen = message is not null;
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        var url = Settings.NormalizeServerUrl(ServerBox.Text);
        if (url is null)
        {
            ShowSignInError("Enter the server's address, like notes.example.com.");
            return;
        }
        ShowSignInError(null);
        SetSigningIn(true);
        _signIn = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await _session.SignIn(url, _signIn.Token);
            Native.BringToFront(this);
        }
        catch (OperationCanceledException)
        {
            // Cancelled, or the browser was never finished.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ShowSignInError($"Couldn't sign in: {ex.Message}");
        }
        finally
        {
            SetSigningIn(false);
            _signIn.Dispose();
            _signIn = null;
        }
    }

    private void SetSigningIn(bool busy)
    {
        SignInButton.IsEnabled = !busy;
        ServerBox.IsEnabled = !busy;
        SignInProgress.IsActive = busy;
        CancelSignInButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SignInHint.Visibility = CancelSignInButton.Visibility;
    }

    private void OnCancelSignIn(object sender, RoutedEventArgs e) => _signIn?.Cancel();

    private void OnServerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && SignInButton.IsEnabled) OnSignIn(sender, e);
    }

    private async void OnSignOut(object sender, RoutedEventArgs e) => await _session.SignOut();

    private void OnSyncNow(object sender, RoutedEventArgs e) => _session.Sync?.RequestSync();

    // --- the list ------------------------------------------------------------------

    private void Reload()
    {
        var query = new ItemQuery(SearchBox.Text, [.. _activeTags], ArchiveToggle.IsChecked == true);
        var fresh = _session.Store.Query(query).Select(i => new ItemViewModel(i)).ToList();
        Apply(fresh);

        var tags = _session.Store.Tags(query.Archived);
        _activeTags.RemoveAll(t => !tags.Any(c => c.Tag == t));
        if (!tags.SequenceEqual(_shownTags)) RebuildTags(tags);

        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text =
            !string.IsNullOrWhiteSpace(SearchBox.Text) || _activeTags.Count > 0 ? "Nothing matches."
            : query.Archived ? "Nothing archived."
            : "Nothing saved yet. Write a note above, or drop a link anywhere in this window.";

        foreach (var vm in _items) _ = vm.LoadImage(_files, _session.Api);
    }

    /// <summary>Updates the list in place, so scrolling and focus survive a sync.</summary>
    private void Apply(List<ItemViewModel> fresh)
    {
        var ids = fresh.Select(v => v.Id).ToHashSet();
        for (var i = _items.Count - 1; i >= 0; i--)
            if (!ids.Contains(_items[i].Id)) _items.RemoveAt(i);

        for (var i = 0; i < fresh.Count; i++)
        {
            var next = fresh[i];
            var at = -1;
            for (var j = i; j < _items.Count; j++)
            {
                if (_items[j].Id != next.Id) continue;
                at = j;
                break;
            }
            if (at < 0)
            {
                _items.Insert(i, next);
                continue;
            }
            if (at != i) _items.Move(at, i);
            if (!_items[i].SameAs(next))
            {
                next.ReuseImageFrom(_items[i]);
                _items[i] = next;
            }
        }
    }

    private void RebuildTags(IReadOnlyList<TagCount> tags)
    {
        _shownTags = tags;
        TagBar.Children.Clear();
        foreach (var tag in tags)
        {
            var button = new ToggleButton { Content = $"#{tag.Tag}", IsChecked = _activeTags.Contains(tag.Tag), Tag = tag.Tag };
            ToolTipService.SetToolTip(button, $"{tag.Count} item{(tag.Count == 1 ? "" : "s")}");
            button.Click += OnTagClick;
            TagBar.Children.Add(button);
        }
    }

    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        var tag = (string)button.Tag;
        if (button.IsChecked == true) _activeTags.Add(tag);
        else _activeTags.Remove(tag);
        Reload();
    }

    private void OnArchiveToggle(object sender, RoutedEventArgs e)
    {
        _activeTags.Clear();
        _shownTags = [];
        Reload();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => _reloadTimer.Start();

    // --- quick add -------------------------------------------------------------------

    private void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        // Enter saves, like a chat box; Shift+Enter and Enter inside a ``` code block add a line.
        if (shift || InsideCodeFence(Composer.Text, Composer.SelectionStart)) return;
        e.Handled = true;
        Save();
    }

    internal static bool InsideCodeFence(string text, int caret)
    {
        var before = text[..Math.Min(caret, text.Length)].ReplaceLineEndings("\n");
        return before.Split('\n').Count(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal)) % 2 == 1;
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        var capture = CaptureParser.Parse(Composer.Text);
        if (capture is null) return;
        _session.Store.Save(Item.New(capture.Text, capture.Url, capture.Tags));
        Composer.Text = "";
        if (ArchiveToggle.IsChecked == true)
        {
            ArchiveToggle.IsChecked = false;
            OnArchiveToggle(this, new RoutedEventArgs());
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (FeedPanel.Visibility != Visibility.Visible) return;
        var data = e.DataView;
        if (data.Contains(StandardDataFormats.WebLink) || data.Contains(StandardDataFormats.Uri) || data.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to note";
        }
    }

    /// <summary>A dropped link or text goes into the composer, where a note and #tags can be added before saving.</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        var data = e.DataView;
        string? text = null;
        if (data.Contains(StandardDataFormats.WebLink)) text = (await data.GetWebLinkAsync()).ToString();
        else if (data.Contains(StandardDataFormats.Uri)) text = (await data.GetUriAsync()).ToString();
        else if (data.Contains(StandardDataFormats.Text)) text = await data.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;

        var current = Composer.Text.TrimEnd();
        Composer.Text = current.Length == 0 ? text.Trim() : $"{current}\n{text.Trim()}";
        Composer.Focus(FocusState.Programmatic);
        Composer.SelectionStart = Composer.Text.Length;
    }

    // --- item actions -------------------------------------------------------------------

    private static ItemViewModel Vm(object sender) => (ItemViewModel)((FrameworkElement)sender).Tag;

    private void OnMore(object sender, RoutedEventArgs e) => BuildMenu(Vm(sender)).ShowAt((FrameworkElement)sender);

    private void OnCardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var options = new FlyoutShowOptions();
        if (args.TryGetPosition(sender, out var point)) options.Position = point;
        BuildMenu(Vm(sender)).ShowAt(sender, options);
        args.Handled = true;
    }

    private void OnCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => _ = Edit(Vm(sender).Item);

    private void OnOpenLink(object sender, RoutedEventArgs e) => OpenLink(Vm(sender).Item);

    private void OnOpenFile(object sender, RoutedEventArgs e) => _ = OpenFile(Vm(sender).Item);

    private MenuFlyout BuildMenu(ItemViewModel vm)
    {
        var item = vm.Item;
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }

        if (item.Url is not null) Add("Open link", "\uE8A7", () => OpenLink(item));
        if (item.File is not null) Add("Open file", "\uE8E5", () => _ = OpenFile(item));
        Add("Edit", "\uE70F", () => _ = Edit(item));
        Add(vm.CopyLabel, "\uE8C8", () => Copy(item));
        Add(vm.PinLabel, item.Pinned ? "\uE77A" : "\uE718", () => _session.Store.Save(item with { Pinned = !item.Pinned }));
        Add(vm.ArchiveLabel, "\uE7B8", () => _session.Store.Save(item with { Archived = !item.Archived }));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Delete", "\uE74D", () => Delete(item));
        return menu;
    }

    private static void OpenLink(Item item)
    {
        if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            _ = Launcher.LaunchUriAsync(uri);
    }

    private async Task OpenFile(Item item)
    {
        var path = await _files.Get(_session.Api, item.File!);
        if (path is null)
        {
            ShowNotice("The file couldn't be downloaded. Check that the server is reachable.", InfoBarSeverity.Warning);
            return;
        }
        await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
    }

    private void Copy(Item item)
    {
        var package = new DataPackage();
        package.SetText(
            item.File is not null ? $"{_session.Settings.ServerUrl}/api/files/{item.File.Id}"
            : item.Url ?? item.Text
        );
        Clipboard.SetContent(package);
    }

    private async Task Edit(Item item)
    {
        var box = new TextBox
        {
            Text = CaptureParser.ToText(item),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
            MaxHeight = 480,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Edit",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var capture = CaptureParser.Parse(box.Text);
        // A file item may have no text at all; anything else needs something left in it.
        if (capture is null && item.File is null) return;
        var current = _session.Store.Get(item.Id) ?? item;
        _session.Store.Save(current with { Text = capture?.Text ?? "", Url = capture?.Url, Tags = capture?.Tags ?? [] });
    }

    private void Delete(Item item)
    {
        _lastDeleted = _session.Store.Delete(item);
        ShowNotice("Deleted.", InfoBarSeverity.Informational, undo: true);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_lastDeleted is null) return;
        _session.Store.Save(_lastDeleted with { DeletedAt = null });
        _lastDeleted = null;
        UndoBar.IsOpen = false;
    }

    internal void ShowNotice(string message, InfoBarSeverity severity, bool undo = false)
    {
        UndoBar.Message = message;
        UndoBar.Severity = severity;
        UndoBar.ActionButton.Visibility = undo ? Visibility.Visible : Visibility.Collapsed;
        UndoBar.IsOpen = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }
}
