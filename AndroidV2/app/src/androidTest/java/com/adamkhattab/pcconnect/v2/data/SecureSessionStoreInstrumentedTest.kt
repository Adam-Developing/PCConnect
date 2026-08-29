package com.adamkhattab.pcconnect.v2.data

import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.security.KeyStore
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class SecureSessionStoreInstrumentedTest {
    @Test
    fun refreshTokenIsWrappedByAndroidKeystoreAndCanBeCleared() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val store = SecureSessionStore(context)
        val token = "instrumentation-refresh-token-that-must-not-appear-in-plaintext"
        store.clear()
        store.writeRefreshToken(token)

        assertEquals(token, store.readRefreshToken())
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        assertTrue(keyStore.containsAlias(SecureSessionStore.KeyAlias))
        val persisted = context.filesDir.resolve("datastore/secure_session.preferences_pb")
        assertTrue(persisted.isFile)
        assertFalse(persisted.readBytes().toString(Charsets.ISO_8859_1).contains(token))

        store.clear()
        assertNull(store.readRefreshToken())
    }
}
