package uk.co.adamkhattab.pcconnect.data

import com.google.gson.JsonElement
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import com.microsoft.signalr.HubConnectionState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlin.math.min
import kotlin.random.Random

/**
 * When to poll and how long to wait, while the realtime connection is unhealthy.
 *
 * Ported from the Go agent's `internal/realtime/policy.go`, which the assessment
 * found small, tested and correct, plus the one extension 05 §5 asks for:
 * jitter, so a server restart does not make every client reconnect on the same
 * tick.
 */
class FallbackPollingPolicy(
    private val base: Long = 5_000,
    private val max: Long = 30_000,
) {
    var current: Long = base
        private set

    fun nextInterval(): Long {
        current = min(current * 2, max)
        return jitter(current)
    }

    fun reset(): Long {
        current = base
        return jitter(base)
    }

    companion object {
        /** A connected client does not poll at all. */
        fun shouldPoll(socketHealthy: Boolean): Boolean = !socketHealthy

        fun jitter(interval: Long, fraction: Double = Random.nextDouble(-1.0, 1.0)): Long =
            maxOf(1L, (interval * (1 + (0.2 * fraction.coerceIn(-1.0, 1.0)))).toLong())
    }
}

/**
 * The realtime channel, with polling as the safety net rather than the
 * mechanism (01 §1 G6).
 *
 * The app this replaces polled on a fixed interval from an `AsyncTask` and had
 * no push at all (S2-14).
 */
class RealtimeClient(
    private val api: PcConnectApi,
    private val scope: CoroutineScope,
) {
    private val json = Json { ignoreUnknownKeys = true }
    private val policy = FallbackPollingPolicy()

    private var connection: HubConnection? = null

    private val _connected = MutableStateFlow(false)
    val connected: StateFlow<Boolean> = _connected.asStateFlow()

    private val _commandStatus = MutableSharedFlow<CommandStatusEvent>(extraBufferCapacity = 32)
    val commandStatus: SharedFlow<CommandStatusEvent> = _commandStatus.asSharedFlow()

    private val _presence = MutableSharedFlow<DevicePresenceEvent>(extraBufferCapacity = 32)
    val presence: SharedFlow<DevicePresenceEvent> = _presence.asSharedFlow()

    private val _reminderDue = MutableSharedFlow<ReminderDueEvent>(extraBufferCapacity = 16)
    val reminderDue: SharedFlow<ReminderDueEvent> = _reminderDue.asSharedFlow()

    /** A reminder changed on another of this account's clients (05 §3). */
    private val _reminderChanged = MutableSharedFlow<ReminderChangedEvent>(extraBufferCapacity = 16)
    val reminderChanged: SharedFlow<ReminderChangedEvent> = _reminderChanged.asSharedFlow()

    /** Refreshed on connect, so nothing on screen is timed against the phone's clock. */
    var serverClockOffsetMillis: Long = 0
        private set

    suspend fun start() {
        val discovery = runCatching { api.discovery() }.getOrNull()

        // The server's advertised URL first, the API's own origin second. The
        // fallback matters when the two disagree about what host the client can
        // reach — a phone on a split-DNS network, or an emulator for which the
        // server's "localhost" is the phone itself.
        val candidates = listOfNotNull(
            discovery?.realtimeUrl,
            api.baseUrl.replaceFirst("http", "ws").trimEnd('/') + "/rt",
        ).distinct()

        discovery?.let {
            serverClockOffsetMillis = runCatching {
                java.time.Instant.parse(it.serverTime).toEpochMilli() - System.currentTimeMillis()
            }.getOrDefault(0)
        }

        for (url in candidates) {
            if (tryConnect(url)) return
        }

        AppLog.w(TAG, "Could not connect to any realtime URL; falling back to polling")
    }

    private suspend fun tryConnect(url: String): Boolean {
        val hub = HubConnectionBuilder.create(url)
            // The token is presented in the handshake and verified as a JWT; it
            // is never written to a cookie, which is what the previous design
            // did with `secure:false` (05 §2.1, S1-12).
            .withAccessTokenProvider(
                io.reactivex.rxjava3.core.Single.defer {
                    io.reactivex.rxjava3.core.Single.fromCallable {
                        kotlinx.coroutines.runBlocking { api.accessToken().orEmpty() }
                    }
                },
            )
            .build()

        // JsonElement, not String.
        //
        // The server sends `{v, id, at, data}` as a JSON object (05 §3), and
        // asking the SignalR Java client for a String made it try to read that
        // object as a JSON string. Every event failed to deserialize and was
        // dropped before reaching any of these handlers, silently: the socket
        // connected, the badge said "Live", and not one push ever arrived. The
        // symptom was a screen that only ever updated when the user pulled to
        // refresh — which looked like the polling app this replaced.
        //
        // GSON parses into JsonElement without knowing the shape; kotlinx then
        // decodes the app's own @Serializable types from it, so there is still
        // exactly one serializer for the contract.
        hub.on(
            "command.status",
            { payload -> emit(payload) { envelope: RealtimeEnvelope<CommandStatusEvent> -> _commandStatus.tryEmit(envelope.data) } },
            JsonElement::class.java,
        )

        hub.on(
            "device.presence",
            { payload -> emit(payload) { envelope: RealtimeEnvelope<DevicePresenceEvent> -> _presence.tryEmit(envelope.data) } },
            JsonElement::class.java,
        )

        hub.on(
            "reminder.due",
            { payload -> emit(payload) { envelope: RealtimeEnvelope<ReminderDueEvent> -> _reminderDue.tryEmit(envelope.data) } },
            JsonElement::class.java,
        )

        hub.on(
            "reminder.changed",
            { payload -> emit(payload) { envelope: RealtimeEnvelope<ReminderChangedEvent> -> _reminderChanged.tryEmit(envelope.data) } },
            JsonElement::class.java,
        )

        hub.onClosed {
            _connected.value = false
            AppLog.w(TAG, "Realtime connection closed")
        }

        connection = hub

        return withContext(Dispatchers.IO) {
            runCatching { hub.start().blockingAwait() }
                .onSuccess {
                    _connected.value = true
                    policy.reset()
                    AppLog.i(TAG, "Realtime connected to $url")
                }
                .onFailure { AppLog.w(TAG, "Realtime connect to $url failed", it) }
                .isSuccess
        }
    }

    private inline fun <reified T> emit(payload: JsonElement, crossinline handler: (T) -> Unit) {
        runCatching { json.decodeFromString<T>(payload.toString()) }
            .onSuccess { handler(it) }
            .onFailure { AppLog.w(TAG, "Could not decode a realtime payload", it) }
    }

    /**
     * The recovery half of the model: while the socket is unhealthy the client
     * re-reads state on a backed-off, jittered interval; while it is healthy it
     * does not poll at all.
     */
    fun startFallbackLoop(onPoll: suspend () -> Unit) {
        scope.launch {
            while (isActive) {
                val wait = if (FallbackPollingPolicy.shouldPoll(_connected.value)) {
                    runCatching { onPoll() }

                    if (connection?.connectionState == HubConnectionState.DISCONNECTED) {
                        runCatching {
                            withContext(Dispatchers.IO) { connection?.start()?.blockingAwait() }
                            _connected.value = connection?.connectionState == HubConnectionState.CONNECTED

                            // Logged here as well as in tryConnect: this is the
                            // path a reconnect actually takes, and "the socket
                            // came back" is the line someone reading the log to
                            // explain a gap is looking for.
                            if (_connected.value) AppLog.i(TAG, "Realtime reconnected")

                            // SignalR replays nothing that was sent while the
                            // socket was down, so reconnecting is only half the
                            // recovery: without this read the screen would keep
                            // showing whatever was true when the connection
                            // dropped, and stop polling because it now looks
                            // healthy (05 §6).
                            if (_connected.value) {
                                runCatching { onPoll() }
                            }
                        }
                    }

                    if (_connected.value) policy.reset() else policy.nextInterval()
                } else {
                    policy.reset()
                }

                delay(wait)
            }
        }
    }

    suspend fun stop() {
        withContext(Dispatchers.IO) {
            runCatching { connection?.stop()?.blockingAwait() }
        }
        _connected.value = false
        connection = null
    }

    private companion object {
        const val TAG = "PCConnect.Realtime"
    }
}
