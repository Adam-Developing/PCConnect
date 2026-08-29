package com.adamkhattab.pcconnect.v2.sync

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.Constraints
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.adamkhattab.pcconnect.v2.data.ControllerRepository
import java.util.concurrent.TimeUnit

class ControllerSyncWorker(
    context: Context,
    parameters: WorkerParameters,
    private val repository: ControllerRepository,
) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        if (!repository.hasSession()) return Result.success()
        return runCatching { repository.recoverAll() }
            .fold(onSuccess = { Result.success() }, onFailure = { Result.retry() })
    }

    companion object {
        private const val name = "controller-read-model-recovery"

        fun schedule(context: Context) {
            val request = PeriodicWorkRequestBuilder<ControllerSyncWorker>(15, TimeUnit.MINUTES)
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(name, ExistingPeriodicWorkPolicy.UPDATE, request)
        }
    }
}
