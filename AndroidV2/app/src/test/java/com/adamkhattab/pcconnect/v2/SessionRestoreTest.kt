package com.adamkhattab.pcconnect.v2

import java.io.IOException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class SessionRestoreTest {
    @Test
    fun `temporary recovery failure retains the session for background retry`() = runTest {
        val result = restorePersistedSession(
            refreshAndRecover = { throw IOException("synthetic outage") },
            hasSession = { true },
        )

        assertEquals(SessionRestoreResult.RETRY_IN_BACKGROUND, result)
    }

    @Test
    fun `rejected refresh signs out after the credential is cleared`() = runTest {
        val result = restorePersistedSession(
            refreshAndRecover = { throw IllegalStateException("refresh rejected") },
            hasSession = { false },
        )

        assertEquals(SessionRestoreResult.NO_SESSION, result)
    }

    @Test
    fun `successful recovery does not recheck or discard the session`() = runTest {
        var sessionChecked = false
        val result = restorePersistedSession(
            refreshAndRecover = { },
            hasSession = {
                sessionChecked = true
                false
            },
        )

        assertEquals(SessionRestoreResult.RESTORED, result)
        assertFalse(sessionChecked)
    }
}
