package com.adamkhattab.pcconnect.v2.data

import android.content.Context
import androidx.work.WorkerFactory
import androidx.work.WorkerParameters
import androidx.work.ListenableWorker
import com.adamkhattab.pcconnect.v2.sync.ControllerSyncWorker

class AppContainer(context: Context) {
    val database = AppDatabase.create(context)
    val localPiiCipher = LocalPiiCipher()
    val secureSessionStore = SecureSessionStore(context)
    val tokenManager = TokenManager(secureSessionStore)
    val apiClient = ApiClient(tokenManager)
    val repository = ControllerRepository(apiClient.authenticated, apiClient.anonymous, database.readModel(), tokenManager, localPiiCipher)
    val passkeyClient = PasskeyClient(apiClient.anonymous, apiClient.authenticated, apiClient.json)
    val realtime = RealtimeRecoveryClient(tokenManager, repository)

    init {
        tokenManager.refreshCall = apiClient.anonymous::refresh
    }

    val workerFactory = object : WorkerFactory() {
        override fun createWorker(
            appContext: Context,
            workerClassName: String,
            workerParameters: WorkerParameters,
        ): ListenableWorker? = when (workerClassName) {
            ControllerSyncWorker::class.java.name -> ControllerSyncWorker(appContext, workerParameters, repository)
            else -> null
        }
    }
}
