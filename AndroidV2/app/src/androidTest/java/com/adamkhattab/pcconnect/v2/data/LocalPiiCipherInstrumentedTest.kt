package com.adamkhattab.pcconnect.v2.data

import androidx.test.ext.junit.runners.AndroidJUnit4
import javax.crypto.AEADBadTagException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class LocalPiiCipherInstrumentedTest {
    private val cipher = LocalPiiCipher()

    @Test
    fun reminderTextRoundTripsWithoutAppearingInTheStoredValue() {
        val encoded = cipher.encryptReminder("reminder-1", "private appointment")

        assertNotEquals("private appointment", encoded)
        assertEquals("private appointment", cipher.decryptReminder("reminder-1", encoded))
    }

    @Test
    fun ciphertextCannotBeMovedToAnotherReminder() {
        val encoded = cipher.encryptReminder("reminder-1", "private appointment")

        assertThrows(AEADBadTagException::class.java) {
            cipher.decryptReminder("reminder-2", encoded)
        }
    }
}
