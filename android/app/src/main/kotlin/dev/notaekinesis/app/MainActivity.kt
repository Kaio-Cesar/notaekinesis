package dev.notaekinesis.app

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import dev.notaekinesis.app.ui.FeedScreen
import dev.notaekinesis.app.ui.FeedViewModel
import dev.notaekinesis.app.ui.NotaekinesisTheme
import dev.notaekinesis.app.ui.SignInScreen
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch
import java.io.IOException

class MainActivity : ComponentActivity() {
    private var signingIn by mutableStateOf(false)
    private var signInError by mutableStateOf<String?>(null)
    private var permissionAnswer: CompletableDeferred<Boolean>? = null
    private val askPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        permissionAnswer?.complete(it)
    }
    private var checkedLocalNetwork = false

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        if (savedInstanceState == null) handle(intent)

        setContent {
            NotaekinesisTheme {
                val state by session.state.collectAsStateWithLifecycle()
                if (state.signedIn) {
                    val vm: FeedViewModel = viewModel(factory = viewModelFactory { initializer { FeedViewModel(session) } })
                    FeedScreen(vm, onSignOut = { lifecycleScope.launch { session.signOut() } })
                } else {
                    SignInScreen(state.serverUrl, signingIn, signInError, ::signIn)
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        // Signed in before the app needed the permission (or it was revoked since): ask once per launch.
        val state = session.state.value
        val server = state.serverUrl
        if (!state.signedIn || server == null || checkedLocalNetwork) return
        checkedLocalNetwork = true
        run { if (ensureLocalNetwork(server)) session.state.value.sync?.requestSync() }
    }

    /** True when [server] is reachable as far as permissions go: asks for local network access if it's on the LAN. */
    private suspend fun ensureLocalNetwork(server: String): Boolean {
        if (LocalNetwork.granted(this) || !LocalNetwork.isLocal(server)) return true
        val answer = CompletableDeferred<Boolean>()
        permissionAnswer = answer
        askPermission.launch(LocalNetwork.PERMISSION)
        return answer.await()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    private fun signIn(input: String) {
        val server = AppSession.normalize(input)
        if (server == null) {
            signInError = "That doesn't look like a web address."
            return
        }
        signInError = null
        run {
            signingIn = true
            val allowed = try {
                ensureLocalNetwork(server)
            } finally {
                signingIn = false
            }
            if (allowed) {
                openLink(session.beginSignIn(server).toString())
            } else {
                signInError = "This server is on your local network. Allow \"Nearby devices\" for notaekinesis in the app's settings to reach it."
            }
        }
    }

    private fun handle(intent: Intent) {
        val data = intent.data
        // Back from the server's /native-login page.
        if (intent.action == Intent.ACTION_VIEW && data?.scheme == "notaekinesis" && data.host == "auth") {
            run {
                signingIn = true
                signInError = null
                try {
                    session.completeSignIn(data)
                } catch (e: IOException) {
                    signInError = e.message ?: "Couldn't sign in."
                } finally {
                    signingIn = false
                }
            }
        }
        // Development only: adb shell am start -n dev.notaekinesis.debug/dev.notaekinesis.app.MainActivity --es dev_server URL --es dev_token TOKEN
        val devServer = intent.getStringExtra("dev_server")
        val devToken = intent.getStringExtra("dev_token")
        if (isDebuggable && devServer != null && devToken != null) {
            run {
                try {
                    session.signInWithToken(AppSession.normalize(devServer)!!, devToken)
                } catch (e: IOException) {
                    signInError = e.message
                }
            }
        }
    }

    private fun run(block: suspend () -> Unit) {
        lifecycleScope.launch { block() }
    }
}
