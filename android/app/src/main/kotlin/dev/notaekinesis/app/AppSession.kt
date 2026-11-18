package dev.notaekinesis.app

import android.content.Context
import android.net.Uri
import androidx.core.content.edit
import androidx.core.net.toUri
import androidx.sqlite.driver.bundled.BundledSQLiteDriver
import dev.notaekinesis.core.FileInfo
import dev.notaekinesis.core.Item
import dev.notaekinesis.core.ServerUrl
import dev.notaekinesis.core.api.ApiClient
import dev.notaekinesis.core.api.ApiJson
import dev.notaekinesis.core.api.User
import dev.notaekinesis.core.data.LocalStore
import dev.notaekinesis.core.sync.SyncEngine
import dev.notaekinesis.core.sync.SyncStatus
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.RequestBody
import java.io.File
import java.io.IOException
import java.security.SecureRandom
import java.util.concurrent.TimeUnit

/** Signed in to [serverUrl] as [user]; [api] and [sync] are set exactly when signed in. */
data class SessionState(val serverUrl: String?, val user: User?, val api: ApiClient?, val sync: SyncEngine?) {
    val signedIn get() = sync != null
}

/**
 * Everything that lives as long as the process: settings, the local database and, once signed in,
 * the API client and sync engine. The local database survives sign-out so nothing unsynced is
 * lost; it is cleared only when a different account signs in.
 */
class AppSession(private val context: Context) {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    val store = LocalStore(BundledSQLiteDriver(), File(context.filesDir, "notaekinesis.db").path)
    private val prefs = context.getSharedPreferences("settings", Context.MODE_PRIVATE)
    private val tokens = TokenStore(context)

    /** Shared connection pool for the API and images. */
    val http: OkHttpClient = OkHttpClient.Builder().connectTimeout(15, TimeUnit.SECONDS).build()

    private val _state = MutableStateFlow(SessionState(prefs.getString("server", null), loadUser(), null, null))
    val state: StateFlow<SessionState> = _state.asStateFlow()

    private var syncJob: Job? = null
    private var foreground = false

    @OptIn(ExperimentalCoroutinesApi::class)
    val syncStatus: kotlinx.coroutines.flow.Flow<SyncStatus> =
        _state.flatMapLatest { it.sync?.status ?: flowOf(SyncStatus.SignedOut) }

    private fun loadUser(): User? = prefs.getString("user", null)?.let {
        runCatching { ApiJson.decodeFromString(User.serializer(), it) }.getOrNull()
    }

    /** Resumes the saved session, if there is one. */
    fun resume() {
        val s = _state.value
        if (s.signedIn || s.serverUrl == null || s.user == null) return
        val token = tokens.read(s.serverUrl) ?: return
        start(s.serverUrl, s.user, token)
    }

    /**
     * For development: debug builds can be handed a server and session token
     * (`adb shell am start ... --es dev_server URL --es dev_token TOKEN`), skipping the browser login.
     */
    suspend fun signInWithToken(serverUrl: String, token: String) {
        val user = ApiClient(serverUrl, token, http).me()
        finishSignIn(serverUrl, user, token)
    }

    // --- browser sign-in -------------------------------------------------------------

    /** Returns the page to open in a Custom Tab. The state is saved, since Android may stop the app while the browser is open. */
    fun beginSignIn(serverUrl: String): Uri {
        val state = ByteArray(24).also { SecureRandom().nextBytes(it) }
            .let { android.util.Base64.encodeToString(it, android.util.Base64.URL_SAFE or android.util.Base64.NO_WRAP or android.util.Base64.NO_PADDING) }
        prefs.edit {
            putString("pending_server", serverUrl)
            putString("pending_state", state)
        }
        return "$serverUrl/native-login".toUri().buildUpon()
            .appendQueryParameter("redirect", REDIRECT)
            .appendQueryParameter("state", state)
            .build()
    }

    /** Handles notaekinesis://auth?token=…&state=… from the login page. */
    suspend fun completeSignIn(redirect: Uri) {
        val server = prefs.getString("pending_server", null)
        val expected = prefs.getString("pending_state", null)
        val token = redirect.getQueryParameter("token")?.ifBlank { null }
        val state = redirect.getQueryParameter("state")?.ifBlank { null }
        if (server == null || expected == null || token == null || state == null ||
            !java.security.MessageDigest.isEqual(expected.toByteArray(), state.toByteArray())
        ) throw IOException("This sign-in link doesn't match the sign-in that was started. Try again.")
        prefs.edit {
            remove("pending_server")
            remove("pending_state")
        }
        val verified = ApiClient(server, null, http).verifyOneTimeToken(token)
        finishSignIn(server, verified.user, verified.session.token)
    }

    private suspend fun finishSignIn(serverUrl: String, user: User, token: String) = withContext(Dispatchers.IO) {
        val s = _state.value
        // A different account (or server) shouldn't see the previous one's notes.
        if (s.serverUrl != serverUrl || s.user?.id != user.id) store.reset()
        tokens.write(serverUrl, token)
        prefs.edit {
            putString("server", serverUrl)
            putString("user", ApiJson.encodeToString(User.serializer(), user))
        }
        stopSync()
        start(serverUrl, user, token)
    }

    suspend fun signOut() {
        val s = _state.value
        s.api?.let { api -> runCatching { withContext(Dispatchers.IO) { api.signOut() } } }
        s.serverUrl?.let(tokens::delete)
        stopSync()
        SyncWorker.cancel(context)
    }

    private fun start(serverUrl: String, user: User, token: String) {
        val api = ApiClient(serverUrl, token, http)
        val job = SupervisorJob(scope.coroutineContext[Job])
        syncJob = job
        val engineScope = CoroutineScope(scope.coroutineContext + job)
        val sync = SyncEngine(store, api, engineScope)
        sync.start()
        sync.listen(foreground)
        _state.value = SessionState(serverUrl, user, api, sync)
        SyncWorker.schedulePeriodic(context)
        scope.launch { refreshUser(api) }
        // The server revoked or expired the session: back to the sign-in screen.
        engineScope.launch {
            sync.status.first { it == SyncStatus.SignedOut }
            tokens.delete(serverUrl)
            stopSync()
            SyncWorker.cancel(context)
        }
    }

    private fun stopSync() {
        syncJob?.cancel()
        syncJob = null
        val s = _state.value
        _state.value = s.copy(api = null, sync = null)
    }

    /** Picks up a new name or picture from the identity provider. */
    private suspend fun refreshUser(api: ApiClient) {
        val user = runCatching { api.me() }.getOrNull() ?: return
        if (user == _state.value.user) return
        prefs.edit { putString("user", ApiJson.encodeToString(User.serializer(), user)) }
        _state.value = _state.value.copy(user = user)
    }

    /** The live event stream runs only while the app is on screen; in the background WorkManager takes over. */
    fun setForeground(on: Boolean) {
        foreground = on
        val sync = _state.value.sync ?: return
        sync.listen(on)
        if (on) sync.requestSync()
        else if (store.pendingCount > 0) SyncWorker.runSoon(context)
    }

    /** Uploads a file and saves it as a new item. Needs a connection: the item points at the server's copy. */
    suspend fun saveFile(content: RequestBody, name: String, extra: Item.() -> Item = { this }): Item {
        val api = _state.value.api ?: throw IOException("Sign in first.")
        val file = api.upload(content, name)
        return store.save(Item.new().copy(file = FileInfo(file.id, file.name, file.mime, file.size)).extra())
    }

    fun close() {
        scope.cancel()
        store.close()
    }

    companion object {
        const val REDIRECT = "notaekinesis://auth"

        fun normalize(input: String) = ServerUrl.normalize(input)
    }
}
