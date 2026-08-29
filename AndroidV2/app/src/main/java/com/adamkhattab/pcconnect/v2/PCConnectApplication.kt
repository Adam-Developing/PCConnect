package com.adamkhattab.pcconnect.v2

import android.app.Application
import androidx.work.Configuration
import com.adamkhattab.pcconnect.v2.data.AppContainer

class PCConnectApplication : Application(), Configuration.Provider {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this)
    }

    override val workManagerConfiguration: Configuration
        get() = Configuration.Builder()
            .setWorkerFactory(container.workerFactory)
            .build()
}
