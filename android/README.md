# notaekinesis for Android

A native Kotlin + Jetpack Compose client (Material 3, dynamic color). It keeps a full copy of your notes in a local SQLite database, so it opens instantly and works offline; changes sync with the server in the background.

- `core`: local database (bundled SQLite with FTS5, the same schema and SQL as the Windows client), sync engine, API client, and parsing of quick-add text, shared text and Markdown. Plain Kotlin/JVM, so its tests run without a device.
- `app`: the Android app: feed, sign-in, share target, background sync.

## Requirements

- JDK 21
- Android SDK (Android Studio's, or set `sdk.dir` in `local.properties`). The build downloads the platform it needs if the SDK licences are accepted.

## Build, test, run

```bash
./gradlew :core:test
```

```bash
./gradlew :app:installDebug
```

```bash
./gradlew :app:assembleRelease
```

The release APK lands in `app/build/outputs/apk/release/`. Debug builds install alongside it as `dev.notaekinesis.debug`.

Release builds are signed with the key named by `ANDROID_KEYSTORE_PATH`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`, if set; otherwise with the debug key, which is fine for sideloading but can't update an APK signed by another machine. `-PversionName=1.2.3 -PversionCode=…` set the version. The release workflow (`.github/workflows/release.yml`) does all of this from a `v1.2.3` tag, taking the key from repository secrets.

## Using it

- **Feed**: pinned first, then newest. Search matches word prefixes and ignores accents; tap a tag to filter. Swipe right to archive, left to delete (both with Undo). Long-press for pin, edit, copy and share.
- **Adding**: type in the box at the bottom, with a link on the first line and `#tags` anywhere, the same rules as the web app. The paper clip uploads images, videos and PDFs.
- **Share to notaekinesis** from any app: a link, text, or files. It's saved at once, and a sheet lets you add a note or `#tags`. Shared text is kept as it is (a `#` in someone else's text isn't read as a tag). A shared link takes the page title the app sends along.
- **Offline**: links and notes are saved locally and sync later. Files need a connection, because they upload straight away.

## Sync

While the app is on screen it keeps the server's event stream open, so changes from other devices show up within a second. In the background WorkManager syncs every 15 minutes, and once soon after you leave the app with changes not yet pushed. There's no Google push service involved.

## Signing in

Enter the server address and choose **Sign in**. A Custom Tab opens the server's `/native-login` page, which runs the usual SSO login and sends the browser back to `notaekinesis://auth` with a one-time token. A random `state` ties that back to the sign-in the app started. The app trades the token for a session token, encrypted with a key kept in the Android Keystore. **Sign out** revokes the session on the server.

Plain `http://` addresses are allowed, for servers on the LAN or a VPN.

## Development

Point a debug build at a local server without the browser login. In the emulator, the host machine is `10.0.2.2`. The token comes from `bun run dev:login` in `server/`.

```bash
adb shell am start -n dev.notaekinesis.debug/dev.notaekinesis.app.MainActivity --es dev_server http://10.0.2.2:3000 --es dev_token <bearer token>
```

Release builds ignore these extras.
