package dev.notaekinesis.app

import android.app.Application
import android.content.Context
import android.content.pm.ApplicationInfo
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import coil3.svg.SvgDecoder
import okhttp3.Call

class App : Application(), SingletonImageLoader.Factory {
    lateinit var session: AppSession
        private set

    override fun onCreate() {
        super.onCreate()
        session = AppSession(this)
        session.resume()
        ProcessLifecycleOwner.get().lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) = session.setForeground(true)
            override fun onStop(owner: LifecycleOwner) = session.setForeground(false)
        })
    }

    /** Images (attachments and favicons) load through the API client, so attachments carry the session. */
    override fun newImageLoader(context: PlatformContext): ImageLoader = ImageLoader.Builder(context)
        .components {
            add(SvgDecoder.Factory()) // profile pictures can be SVG (e.g. the dev user's)
            add(OkHttpNetworkFetcherFactory(callFactory = {
                Call.Factory { request -> (session.state.value.api?.http ?: session.http).newCall(request) }
            }))
        }
        .build()
}

val Context.session: AppSession get() = (applicationContext as App).session

val Context.isDebuggable: Boolean get() = applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0
