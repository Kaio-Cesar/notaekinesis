package dev.notaekinesis.app

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
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
import kotlinx.coroutines.launch
import java.io.IOException

class MainActivity : ComponentActivity() {
    private var signingIn by mutableStateOf(false)
    private var signInError by mutableStateOf<String?>(null)

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
        openLink(session.beginSignIn(server).toString())
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
