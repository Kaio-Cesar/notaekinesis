package dev.notaekinesis.app

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import dev.notaekinesis.core.api.UnauthorizedException
import java.io.IOException
import java.util.concurrent.TimeUnit

/**
 * Sync while the app isn't on screen: every 15 minutes (Android's minimum), and once soon after
 * leaving the app with changes not yet pushed. No push service is involved.
 */
class SyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val session = (applicationContext as App).session
        session.resume()
        val sync = session.state.value.sync ?: return Result.success()
        return try {
            sync.syncOnce()
            Result.success()
        } catch (_: UnauthorizedException) {
            Result.success() // the app shows the sign-in screen next time it opens
        } catch (_: IOException) {
            Result.retry()
        }
    }

    companion object {
        private val online = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()

        fun schedulePeriodic(context: Context) {
            val request = PeriodicWorkRequestBuilder<SyncWorker>(15, TimeUnit.MINUTES).setConstraints(online).build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork("sync", ExistingPeriodicWorkPolicy.KEEP, request)
        }

        fun runSoon(context: Context) {
            val request = OneTimeWorkRequestBuilder<SyncWorker>().setConstraints(online).build()
            WorkManager.getInstance(context).enqueueUniqueWork("sync-now", ExistingWorkPolicy.REPLACE, request)
        }

        fun cancel(context: Context) {
            WorkManager.getInstance(context).cancelUniqueWork("sync")
            WorkManager.getInstance(context).cancelUniqueWork("sync-now")
        }
    }
}
