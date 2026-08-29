package com.adamkhattab.pcconnect.v2.data

import com.adamkhattab.pcconnect.v2.BuildConfig
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import io.reactivex.rxjava3.core.Single
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking

data class RealtimeEnvelope(
    val eventId: String,
    val eventType: String,
    val entityId: String,
    val entityVersion: Long,
    val occurredAt: String,
    val payload: Map<String, Any?>,
)

/** SignalR is advisory. Every hint and every reconnect triggers authoritative REST recovery. */
class RealtimeRecoveryClient(
    private val tokens: TokenManager,
    private val repository: ControllerRepository,
) {
    private var scope: CoroutineScope? = null
    private var hub: HubConnection? = null
    private val started = AtomicBoolean(false)
    private val connecting = AtomicBoolean(false)

    fun start() {
        if (!started.compareAndSet(false, true)) return
        val activeScope = CoroutineScope(SupervisorJob() + Dispatchers.IO).also { scope = it }
        val connection = HubConnectionBuilder
            .create(BuildConfig.API_BASE_URL + "hubs/controller")
            .withAccessTokenProvider(Single.fromCallable { runBlocking { tokens.refresh().orEmpty() } })
            .build()

        connection.on("CommandStatusChanged", { _: RealtimeEnvelope ->
            activeScope.launch { runCatching { repository.recoverCommands() } }
        }, RealtimeEnvelope::class.java)
        connection.on("DevicePresenceChanged", { _: RealtimeEnvelope ->
            activeScope.launch { runCatching { repository.recoverDevices() } }
        }, RealtimeEnvelope::class.java)
        connection.on("ReminderChanged", { _: RealtimeEnvelope ->
            activeScope.launch { runCatching { repository.recoverReminders() } }
        }, RealtimeEnvelope::class.java)
        connection.on("SessionRevoked", { _: RealtimeEnvelope ->
            activeScope.launch { tokens.clear(); stop() }
        }, RealtimeEnvelope::class.java)
        connection.onClosed {
            if (started.get()) activeScope.launch { connectWithBackoff(connection) }
        }
        hub = connection
        activeScope.launch { connectWithBackoff(connection) }
    }

    fun stop() {
        started.set(false)
        runCatching { hub?.stop()?.blockingAwait() }
        hub = null
        scope?.cancel()
        scope = null
    }

    private suspend fun connectWithBackoff(connection: HubConnection) {
        if (!connecting.compareAndSet(false, true)) return
        try {
            var delayMillis = 1_000L
            while (started.get()) {
                val connected = runCatching {
                    connection.start().blockingAwait()
                    repository.recoverAll()
                }.isSuccess
                if (connected) return
                delay(delayMillis)
                delayMillis = (delayMillis * 2).coerceAtMost(30_000L)
            }
        } finally {
            connecting.set(false)
        }
    }
}
