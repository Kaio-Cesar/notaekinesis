package dev.notaekinesis.core.sync

import dev.notaekinesis.core.api.ApiClient
import dev.notaekinesis.core.api.UnauthorizedException
import dev.notaekinesis.core.data.LocalStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.IOException
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

enum class SyncStatus { Idle, Syncing, Offline, SignedOut }

/**
 * Keeps the local store and the server in step. There is no polling: it syncs when something is
 * saved locally, when the server's event stream reports a new revision, and whenever the event
 * stream (re)connects. While offline it retries with backoff. The event stream only runs while
 * [listen] is on (on Android: while the app is in the foreground).
 */
class SyncEngine(private val store: LocalStore, private val api: ApiClient, private val scope: CoroutineScope) {
    private val wake = Channel<Unit>(Channel.CONFLATED)
    private val running = Mutex()
    private var worker: Job? = null
    private var listener: Job? = null

    private val _status = MutableStateFlow(SyncStatus.Idle)
    val status: StateFlow<SyncStatus> = _status.asStateFlow()

    var lastError: Throwable? = null
        private set

    fun start() {
        if (worker != null) return
        worker = scope.launch { work() }
        scope.launch { store.localWrites.collect { requestSync() } }
        requestSync()
    }

    /** Asks for a sync soon. Requests made while one is running cause one more run after it. */
    fun requestSync() {
        wake.trySend(Unit)
    }

    /** Turns the live event stream on or off. */
    fun listen(on: Boolean) {
        if (on && listener == null) listener = scope.launch { listenLoop() }
        if (!on) {
            listener?.cancel()
            listener = null
        }
    }

    private fun setStatus(status: SyncStatus, error: Throwable? = null) {
        lastError = error
        _status.value = status
    }

    private suspend fun work() {
        var backoff = 2.seconds
        for (ignored in wake) {
            if (_status.value == SyncStatus.SignedOut) return
            // Let a burst of edits settle into one push.
            delay(150.milliseconds)
            try {
                setStatus(SyncStatus.Syncing)
                syncOnce()
                setStatus(SyncStatus.Idle)
                backoff = 2.seconds
            } catch (e: UnauthorizedException) {
                setStatus(SyncStatus.SignedOut, e)
                return
            } catch (e: IOException) {
                setStatus(SyncStatus.Offline, e)
                retryLater(backoff)
                backoff = minOf(backoff * 2, 300.seconds)
            }
        }
    }

    private fun retryLater(delay: Duration) {
        scope.launch {
            delay(delay)
            requestSync()
        }
    }

    /** Push everything pending, then pull everything new. Safe to call from a background job too. */
    suspend fun syncOnce() = running.withLock {
        while (true) {
            val batch = store.pendingMutations()
            if (batch.isEmpty()) break
            val res = api.push(batch)
            store.completePush(res.results)
            if (batch.size < 500) break
        }
        var since = store.lastRev
        while (true) {
            val page = api.pull(since, 500)
            store.applyRemote(page.items.map { it.toItem() }, page.nextRev)
            since = page.nextRev
            if (!page.hasMore) break
        }
    }

    private suspend fun listenLoop() {
        var backoff = 2.seconds
        while (true) {
            try {
                api.events().collect { rev ->
                    backoff = 2.seconds
                    // The first event comes right after connecting, so a reconnect always catches up.
                    if (rev > store.lastRev || store.pendingCount > 0) requestSync()
                }
            } catch (e: UnauthorizedException) {
                setStatus(SyncStatus.SignedOut, e)
                return
            } catch (e: IOException) {
                setStatus(SyncStatus.Offline, e)
            }
            delay(backoff)
            backoff = minOf(backoff * 2, 120.seconds)
        }
    }
}
