package com.adamkhattab.pcconnect.v2.data

import java.io.IOException
import java.time.Instant
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class RecoveryAndCommandPolicyTest {
    @Test
    fun `API timestamps accept dotnet fractional seconds with a UTC offset`() {
        assertEquals(
            Instant.parse("2026-08-29T14:10:13.249724600Z"),
            parseApiInstant("2026-08-29T14:10:13.2497246+00:00"),
        )
    }

    @Test
    fun `only lock bypasses step-up among supported commands`() {
        assertFalse(CommandPolicy.requiresStepUp("lock"))
        listOf("sleep", "hibernate", "sign_out", "restart", "shutdown").forEach {
            assertTrue(CommandPolicy.requiresStepUp(it))
        }
    }

    @Test
    fun `recovery follows opaque cursors until exhausted`() = runTest {
        val calls = mutableListOf<String?>()
        val result = collectAll<Int> { cursor, _ ->
            calls += cursor
            when (cursor) {
                null -> Page(listOf(1, 2), "opaque-a")
                "opaque-a" -> Page(listOf(3), "opaque-b")
                else -> Page(listOf(4), null)
            }
        }
        assertEquals(listOf(1, 2, 3, 4), result)
        assertEquals(listOf(null, "opaque-a", "opaque-b"), calls)
    }

    @Test
    fun `recovery rejects a repeated cursor`() {
        assertThrows(IllegalStateException::class.java) {
            runTest { collectAll<Int> { _, _ -> Page(emptyList(), "loop") } }
        }
    }

    @Test
    fun `password remains available on API 24 while passkeys start at API 28`() {
        assertFalse(PlatformCapabilities.supportsPasskeys(24))
        assertFalse(PlatformCapabilities.supportsPasskeys(27))
        assertTrue(PlatformCapabilities.supportsPasskeys(28))
        assertTrue(PlatformCapabilities.supportsPasskeys(36))
    }

    @Test
    fun `transient refresh failure retains rotating credential for retry`() = runTest {
        val store = FakeSessionStore("refresh-one")
        val manager = TokenManager(store)
        assertTrue(manager.hasSession())
        assertTrue(manager.sessionAvailable.value)
        manager.refreshCall = { throw IOException("synthetic outage") }
        assertEquals(null, manager.refresh())
        assertEquals("refresh-one", store.value)

        manager.refreshCall = {
            TokenPair("access-two", Instant.now().plusSeconds(600).toString(), "refresh-two",
                Instant.now().plusSeconds(3600).toString(), "00000000-0000-0000-0000-000000000001")
        }
        assertEquals("access-two", manager.refresh())
        assertEquals("refresh-two", store.value)
        assertTrue(manager.sessionAvailable.value)
        manager.clear()
        assertFalse(manager.sessionAvailable.value)
    }

    @Test
    fun `forced refresh rotates even while an access token is still current`() = runTest {
        val store = FakeSessionStore("refresh-one")
        val manager = TokenManager(store)
        var calls = 0
        manager.refreshCall = {
            calls += 1
            TokenPair(
                "access-$calls",
                Instant.now().plusSeconds(600).toString(),
                "refresh-$calls",
                Instant.now().plusSeconds(3600).toString(),
                "00000000-0000-0000-0000-000000000001",
            )
        }

        assertEquals("access-1", manager.refresh())
        assertEquals("access-1", manager.refresh())
        assertEquals(1, calls)
        assertEquals("access-2", manager.refresh(force = true))
        assertEquals(2, calls)
        assertEquals("refresh-2", store.value)
    }

    private class FakeSessionStore(initial: String?) : SessionStore {
        var value: String? = initial
        override suspend fun readRefreshToken(): String? = value
        override suspend fun writeRefreshToken(value: String) { this.value = value }
        override suspend fun clear() { value = null }
    }
}
