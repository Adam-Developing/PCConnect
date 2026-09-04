package uk.co.adamkhattab.pcconnect.data

import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

enum class LogLevel { Debug, Info, Warn, Error }

data class LogEntry(
    val at: Long,
    val level: LogLevel,
    val tag: String,
    val message: String,
) {
    fun timestamp(): String = TimeFormat.format(Date(at))

    override fun toString(): String = "${timestamp()} ${level.name.uppercase(Locale.UK)} $tag: $message"

    private companion object {
        val TimeFormat = SimpleDateFormat("HH:mm:ss.SSS", Locale.UK)
    }
}

/**
 * What the app did, kept where the person using it can see it.
 *
 * The v1 Android app failed silently: a request that did not work produced a
 * blank screen and nothing else, so the only way to find out why was to attach
 * `adb logcat` to the phone. Everything written here also goes to logcat, but it
 * survives in the app itself and can be read and copied from the Account screen.
 *
 * **Nothing secret is ever written here.** No access or refresh token, no device
 * secret, no password, no reminder text, and no request or response body. The
 * log is visible to anyone holding the unlocked phone, which makes it exactly as
 * sensitive as the screen it sits on — so it carries only what is needed to
 * answer "did the request reach the server, and what did the server say".
 */
object AppLog {
    /**
     * Bounded on purpose. An unbounded log on a long-running app is a memory
     * leak with extra steps, and nobody reads the ten-thousandth line.
     */
    private const val CAPACITY = 400

    private val _entries = MutableStateFlow<List<LogEntry>>(emptyList())

    /** Oldest first, so the UI can decide which end to show. */
    val entries: StateFlow<List<LogEntry>> = _entries.asStateFlow()

    fun d(tag: String, message: String) = record(LogLevel.Debug, tag, message, null)

    fun i(tag: String, message: String) = record(LogLevel.Info, tag, message, null)

    fun w(tag: String, message: String, error: Throwable? = null) = record(LogLevel.Warn, tag, message, error)

    fun e(tag: String, message: String, error: Throwable? = null) = record(LogLevel.Error, tag, message, error)

    fun clear() {
        _entries.value = emptyList()
    }

    /** The whole buffer as text, for the clipboard. */
    fun dump(): String = _entries.value.joinToString("\n") { it.toString() }

    private fun record(level: LogLevel, tag: String, message: String, error: Throwable?) {
        // A throwable's message is kept; its stack trace is not. The trace is in
        // logcat for a developer who needs it, and on screen it would push
        // everything else out of a 400-line buffer.
        val text = if (error == null) message else "$message — ${error.javaClass.simpleName}: ${error.message}"

        when (level) {
            LogLevel.Debug -> Log.d(tag, text)
            LogLevel.Info -> Log.i(tag, text)
            LogLevel.Warn -> Log.w(tag, text, error)
            LogLevel.Error -> Log.e(tag, text, error)
        }

        val entry = LogEntry(System.currentTimeMillis(), level, tag, text)

        // update() is a compare-and-set loop, so this is safe from the several
        // threads that log: OkHttp's dispatcher, the SignalR reader, and the UI.
        _entries.update { existing ->
            if (existing.size < CAPACITY) existing + entry else existing.drop(existing.size - CAPACITY + 1) + entry
        }
    }

    private inline fun MutableStateFlow<List<LogEntry>>.update(transform: (List<LogEntry>) -> List<LogEntry>) {
        while (true) {
            val current = value
            if (compareAndSet(current, transform(current))) return
        }
    }
}
