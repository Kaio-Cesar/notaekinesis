# notaekinesis for Windows

A native WinUI 3 client, compiled ahead of time (NativeAOT). It keeps a full copy of your notes in a local SQLite database, so it opens instantly and works offline; changes sync with the server in the background.

- `src/Notaekinesis.Core`: local database (with full-text search), sync engine, API client, sign-in, and parsing of quick-add text, drops and Markdown. No UI, so it is unit-tested on its own.
- `src/Notaekinesis.App`: the WinUI 3 app (unpackaged, Windows App SDK bundled).
- `tests/Notaekinesis.Core.Tests`: xUnit tests for Core.

## Requirements

- .NET SDK 10 (`winget install Microsoft.DotNet.SDK.10`)
- For release builds (NativeAOT): Visual Studio Build Tools 2022 with the "Desktop development with C++" workload. If the publish step can't find `vswhere.exe`, add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH`.

## Build, test, run

```bash
dotnet test
```

```bash
dotnet run --project src/Notaekinesis.App
```

```bash
dotnet publish src/Notaekinesis.App -c Release
```

The release build lands in `src/Notaekinesis.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/`. That folder is the whole app: copy it anywhere and run `Notaekinesis.exe`. For ARM64 add `-r win-arm64`.

## Tray and quick add

Closing the window keeps notaekinesis running in the tray. Click the tray icon to open it again; right-click it for **Quick add** and **Quit**. Starting the app a second time brings back the running one. Start it with `--background` to go straight to the tray (for launching at login).

**Win+Shift+N** opens the quick-add basket near the pointer. It stays up while you switch to another app, so you can:

- drag a link, a browser tab, selected text, or files (images, videos, PDFs) onto it
- paste with Ctrl+V: a copied image or file is saved at once, and copied text goes into the box
- type a quick note with `#tags` and press Enter

You don't need the hotkey while dragging. Drag something to the **right edge of the screen** (the middle half of it, on the outermost monitor) and the basket slides in under the pointer; keep dragging into it and let go. If you move away without dropping, it hides again. A 2-pixel invisible strip catches the drag. It only reacts to drag and drop, so ordinary mouse use is unaffected. You can move it to the left edge, or turn it off, under the account menu → **Drop edge**.

What lands on it is saved immediately. A dragged link keeps the link's text as its title. Plain dropped text is saved as it is, so a `#` in it isn't read as a tag. After a save, you can type a note or `#tags` to add to that item. Esc, or the hotkey again, closes the basket. Files need a connection, because they upload straight away; links and notes don't. If another app already owns Win+Shift+N, the main window says so, and the tray menu still opens the basket.

## Signing in

Enter the server address and choose **Sign in**. Your browser opens the server's `/native-login` page, which runs the usual SSO login and then hands a one-time token to the app through a temporary listener on `127.0.0.1` (RFC 8252). The app trades it for a session token, which is stored in Windows Credential Manager (`notaekinesis:<server>`). **Sign out** revokes the session on the server and removes it from Credential Manager.

## Data

Everything lives in `%LOCALAPPDATA%\Notaekinesis`:

- `notaekinesis.db`: your notes and the queue of changes not yet synced
- `files\`: attachments downloaded for viewing
- `settings.json`: server address and account name
- `crash.log`: written if the app hits an unexpected error

Notes stay on the device after signing out, so nothing unsynced is lost. Signing in with a different account clears them.

## Development

To point the app at a local server without the browser login, pass a session token through the environment. The token comes from `bun run dev:login` in `server/`. It isn't saved anywhere.

```bash
NOTAEKINESIS_DEV_SERVER=http://localhost:3000 NOTAEKINESIS_DEV_TOKEN=<bearer token> NOTAEKINESIS_DATA_DIR=./.devdata dotnet run --project src/Notaekinesis.App
```

`NOTAEKINESIS_DATA_DIR` keeps the development database apart from your real one.

### NativeAOT notes

- Bind with `x:Bind` (compiled), not `{Binding}`; classes used from XAML must be `partial`.
- Don't read theme resources from code (`Application.Current.Resources[...]`): under AOT they come back as untyped objects and casts fail. Set them in XAML, or see `Controls/MarkdownView.cs`.
- Check every change with a release publish. Problems like the above only show up there.
