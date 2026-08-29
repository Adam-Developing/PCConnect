package com.adamkhattab.pcconnect.v2.data

import kotlinx.serialization.encodeToString
import kotlinx.serialization.decodeFromString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ApiSerializationTest {
    @Test
    fun `client descriptor defaults are included in registration payloads`() {
        val payload = PCConnectJson.encodeToString(
            RegistrationRequest(
                username = "local-user",
                email = "local@example.test",
                displayName = "Local User",
                password = "twelve-characters",
                timezone = "Europe/London",
                marketingOptIn = false,
                client = ClientDescriptor(version = "8.0.0"),
            ),
        )

        assertTrue(payload.contains("\"client\":{"))
        assertTrue(payload.contains("\"platform\":\"android\""))
        assertTrue(payload.contains("\"name\":\"PCConnect Android\""))
        assertTrue(payload.contains("\"version\":\"8.0.0\""))
    }

    @Test
    fun `reminder acknowledgement summary is decoded`() {
        val reminder = PCConnectJson.decodeFromString<ReminderDto>(
            """{"id":"reminder-1","text":"Call home","targetMode":"all_devices","targetDeviceIds":[],"timezone":"Europe/London","timezoneAssumed":false,"localStart":"2026-08-29T17:00:00","nextOccurrenceAt":null,"createdAt":"2026-08-29T15:00:00Z","version":2,"lastAcknowledgementStatus":"completed","lastAcknowledgedAt":"2026-08-29T17:00:05Z","lastAcknowledgedBy":"Office PC"}""",
        )

        assertEquals("completed", reminder.lastAcknowledgementStatus)
        assertEquals("2026-08-29T17:00:05Z", reminder.lastAcknowledgedAt)
        assertEquals("Office PC", reminder.lastAcknowledgedBy)
    }
}
