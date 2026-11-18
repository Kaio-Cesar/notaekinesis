package dev.notaekinesis.app.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

// Fallback for phones without Material You (Android 11 and older): the amber of the app icon.
private val Amber = Color(0xFF7A5900)
private val AmberLight = Color(0xFFFABD2F)

/** Material 3 with the wallpaper's colors (dynamic color) where the phone supports it. */
@Composable
fun NotaekinesisTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    val context = LocalContext.current
    val colors = when {
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        dark -> darkColorScheme(primary = AmberLight)
        else -> lightColorScheme(primary = Amber)
    }
    MaterialTheme(colorScheme = colors, content = content)
}
