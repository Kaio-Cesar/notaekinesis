package dev.notaekinesis.app

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.net.toUri
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.Inet6Address
import java.net.InetAddress

/**
 * Android 17 blocks apps that target it from the local network unless they hold
 * ACCESS_LOCAL_NETWORK (a runtime permission in the Nearby devices group). Blocked
 * connections just time out, so a homelab server on the LAN looks unreachable.
 */
object LocalNetwork {
    const val PERMISSION = Manifest.permission.ACCESS_LOCAL_NETWORK
    private const val ANDROID_17 = 37

    fun granted(context: Context): Boolean =
        Build.VERSION.SDK_INT < ANDROID_17 || context.checkSelfPermission(PERMISSION) == PackageManager.PERMISSION_GRANTED

    /** Whether [serverUrl]'s host resolves to a LAN address. False when it can't be resolved. */
    suspend fun isLocal(serverUrl: String): Boolean = withContext(Dispatchers.IO) {
        val host = serverUrl.toUri().host ?: return@withContext false
        val addresses = try {
            InetAddress.getAllByName(host)
        } catch (_: Exception) {
            return@withContext false
        }
        addresses.any { it.isSiteLocalAddress || it.isLinkLocalAddress || it.isUniqueLocal() }
    }

    // fc00::/7; Java's isSiteLocalAddress only knows the deprecated fec0::/10.
    private fun InetAddress.isUniqueLocal() = this is Inet6Address && (address[0].toInt() and 0xfe) == 0xfc
}
