package uk.co.adamkhattab.pcconnect

import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import uk.co.adamkhattab.pcconnect.data.CommandPolicy
import uk.co.adamkhattab.pcconnect.data.CommandTypes
import uk.co.adamkhattab.pcconnect.data.Device
import uk.co.adamkhattab.pcconnect.data.Discovery
import uk.co.adamkhattab.pcconnect.data.FallbackPollingPolicy
import uk.co.adamkhattab.pcconnect.data.PcConnectApi
import uk.co.adamkhattab.pcconnect.data.Reminder
import uk.co.adamkhattab.pcconnect.data.ReminderOccurrenceResponse
import uk.co.adamkhattab.pcconnect.data.UpdateReminderOccurrenceRequest
import uk.co.adamkhattab.pcconnect.ui.AppViewModel
import uk.co.adamkhattab.pcconnect.ui.RepeatKind
import uk.co.adamkhattab.pcconnect.ui.RepeatSpec
import uk.co.adamkhattab.pcconnect.ui.describeRecurrence
import uk.co.adamkhattab.pcconnect.ui.describeRrule
import uk.co.adamkhattab.pcconnect.ui.formatNaturalTime
import uk.co.adamkhattab.pcconnect.ui.isActiveReminder
import uk.co.adamkhattab.pcconnect.ui.rruleFor
import uk.co.adamkhattab.pcconnect.ui.scheduleSummary
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId

class FallbackPollingPolicyTest {

    @Test
    fun `a connected client does not poll at all`() {
        assertFalse(FallbackPollingPolicy.shouldPoll(socketHealthy = true))
        assertTrue(FallbackPollingPolicy.shouldPoll(socketHealthy = false))
    }

    @Test
    fun `the interval doubles up to the cap`() {
        val policy = FallbackPollingPolicy()

        assertEquals(5_000L, policy.current)
        policy.nextInterval()
        assertEquals(10_000L, policy.current)
        policy.nextInterval()
        assertEquals(20_000L, policy.current)
        policy.nextInterval()
        assertEquals(30_000L, policy.current)
        policy.nextInterval()
        assertEquals(30_000L, policy.current)
    }

    @Test
    fun `reset returns to the base interval`() {
        val policy = FallbackPollingPolicy()
        policy.nextInterval()
        policy.reset()
        assertEquals(5_000L, policy.current)
    }

    @Test
    fun `jitter stays within twenty percent`() {
        // Without this a server restart makes every client reconnect on the same
        // tick, which is a self-inflicted thundering herd (05 section 5).
        assertEquals(12_000L, FallbackPollingPolicy.jitter(10_000, 1.0))
        assertEquals(8_000L, FallbackPollingPolicy.jitter(10_000, -1.0))
        assertEquals(10_000L, FallbackPollingPolicy.jitter(10_000, 0.0))

        repeat(200) {
            val sample = FallbackPollingPolicy.jitter(10_000)
            assertTrue("jitter out of band: $sample", sample in 8_000..12_000)
        }
    }
}

class VersionGateTest {

    private val discovery = Discovery(
        apiVersion = "2.0.0",
        realtimeUrl = "wss://api.pcconnect.example/rt",
        minimumSupportedClient = mapOf("mobile" to "8.0.0", "desktop" to "5.0.0"),
        recommendedClient = mapOf("mobile" to "8.1.0"),
        legacySunset = mapOf("v1" to null),
        capabilities = listOf("commands.ttl"),
        serverTime = "2026-09-02T09:00:00Z",
    )

    @Test
    fun `a build below the minimum is told to update`() {
        // This is the lever that eventually lets the legacy endpoints be
        // switched off (04 section 2).
        assertTrue(PcConnectApi.isBelowMinimum(discovery, "7.0.3"))
        assertTrue(PcConnectApi.isBelowMinimum(discovery, "7.9.9"))
        assertFalse(PcConnectApi.isBelowMinimum(discovery, "8.0.0"))
        assertFalse(PcConnectApi.isBelowMinimum(discovery, "8.1.0"))
    }

    @Test
    fun `version comparison handles differing lengths`() {
        assertTrue(PcConnectApi.compareVersions("8.0", "8.0.1") < 0)
        assertTrue(PcConnectApi.compareVersions("8.1", "8.0.9") > 0)
        assertEquals(0, PcConnectApi.compareVersions("8.0.0", "8.0.0"))
    }
}

class CommandVocabularyTest {

    @Test
    fun `the vocabulary is exactly six types`() {
        assertEquals(6, CommandTypes.ALL.size)
        assertEquals(
            setOf("shutdown", "restart", "signout", "lock", "sleep", "hibernate"),
            CommandTypes.ALL.toSet(),
        )
    }

    @Test
    fun `destructive types are the ones that end a session or the power state`() {
        assertEquals(
            setOf("shutdown", "restart", "signout", "hibernate"),
            CommandTypes.DESTRUCTIVE,
        )
        assertFalse("lock" in CommandTypes.DESTRUCTIVE)
        assertFalse("sleep" in CommandTypes.DESTRUCTIVE)
    }
}

class RecurrenceTest {

    private val wednesday = LocalDate.of(2026, 9, 2)

    @Test
    fun `plain words map onto RFC 5545 rules`() {
        assertEquals(null, rruleFor(RepeatSpec(RepeatKind.Once), wednesday))
        assertEquals("FREQ=WEEKLY", rruleFor(RepeatSpec(RepeatKind.Weekly), wednesday))
        assertEquals("FREQ=MONTHLY", rruleFor(RepeatSpec(RepeatKind.Monthly), wednesday))
    }

    @Test
    fun `a custom repeat names its days`() {
        val monWedFri = RepeatSpec(
            kind = RepeatKind.Custom,
            days = listOf(true, false, true, false, true, false, false),
        )

        assertEquals("FREQ=WEEKLY;BYDAY=MO,WE,FR", rruleFor(monWedFri, wednesday))
        assertEquals(
            "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR",
            rruleFor(monWedFri.copy(intervalWeeks = 2), wednesday),
        )
    }

    @Test
    fun `a custom repeat with no day ticked falls back to the day it starts on`() {
        // Otherwise the rule would be FREQ=WEEKLY with no BYDAY, which drifts
        // if the start date is later moved.
        assertEquals("FREQ=WEEKLY;BYDAY=WE", rruleFor(RepeatSpec(RepeatKind.Custom), wednesday))
    }

    @Test
    fun `times never enter the rule`() {
        // BYHOUR and BYMINUTE multiply out: 10:30 and 15:45 in one rule would
        // fire four times a day. Each time is saved as its own series instead.
        val rule = rruleFor(
            RepeatSpec(kind = RepeatKind.Custom, days = listOf(true, false, false, false, false, false, false)),
            wednesday,
        )

        assertFalse(rule!!.contains("BYHOUR"))
        assertFalse(rule.contains("BYMINUTE"))
    }

    @Test
    fun `a rule reads back as the words it was written with`() {
        assertEquals("Every weekday", describeRrule("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"))
        assertEquals("Every week", describeRrule("FREQ=WEEKLY"))
        assertEquals("Every month", describeRrule("FREQ=MONTHLY"))
        assertEquals("Every day", describeRrule("FREQ=DAILY"))
        assertEquals(null, describeRrule(null))
    }

    @Test
    fun `the summary says what was actually configured`() {
        val spec = RepeatSpec(
            kind = RepeatKind.Custom,
            days = listOf(true, true, false, true, false, false, false),
        )

        val summary = scheduleSummary(
            spec = spec,
            date = wednesday,
            times = listOf(LocalTime.of(10, 30), LocalTime.of(15, 30)),
            today = wednesday,
        )

        assertEquals(
            "Every Monday, Tuesday and Thursday at 10:30 and 15:30, starting today. Doesn't end.",
            summary,
        )
    }

    @Test
    fun `a repeat with no day chosen says so rather than saving nothing`() {
        val summary = scheduleSummary(
            spec = RepeatSpec(kind = RepeatKind.Custom),
            date = wednesday,
            times = listOf(LocalTime.of(9, 0)),
            today = wednesday,
        )

        assertEquals("Pick at least one day.", summary)
    }
}

class ContractTest {

    private val json = Json { ignoreUnknownKeys = true }

    @Test
    fun `a server response with an unknown field still decodes`() {
        // Additive changes are not breaking, and clients must ignore what they
        // do not recognise (04 section 2).
        val payload = """
            {
              "id": "01923f4e-0000-7000-8000-000000000000",
              "body": "Buy milk",
              "dueAt": "2026-12-01T09:00:00Z",
              "dueLocalTime": "09:00",
              "timezone": "Europe/London",
              "isCompleted": false,
              "createdAt": "2026-09-02T09:00:00Z",
              "updatedAt": "2026-09-02T09:00:00Z",
              "somethingAddedLater": 42
            }
        """.trimIndent()

        val reminder = json.decodeFromString<Reminder>(payload)

        assertEquals("Buy milk", reminder.body)
        assertEquals("Europe/London", reminder.timezone)
        assertFalse(reminder.isCompleted)
    }
}

class RecurrenceDescriptionTest {

    @Test
    fun `formats natural time in 12-hour and 24-hour clocks`() {
        assertEquals("2 pm", formatNaturalTime(LocalTime.of(14, 0), is24Hour = false))
        assertEquals("14:00", formatNaturalTime(LocalTime.of(14, 0), is24Hour = true))
        assertEquals("2:30 pm", formatNaturalTime(LocalTime.of(14, 30), is24Hour = false))
        assertEquals("14:30", formatNaturalTime(LocalTime.of(14, 30), is24Hour = true))
        assertEquals("12 am", formatNaturalTime(LocalTime.of(0, 0), is24Hour = false))
        assertEquals("00:00", formatNaturalTime(LocalTime.of(0, 0), is24Hour = true))
        assertEquals("12:10 am", formatNaturalTime(LocalTime.of(0, 10), is24Hour = false))
        assertEquals("00:10", formatNaturalTime(LocalTime.of(0, 10), is24Hour = true))
    }

    @Test
    fun `describes weekly, yearly, monthly, daily, and hourly recurrences naturally`() {
        val thursday = LocalDate.of(2026, 10, 8)
        val time2pm = LocalTime.of(14, 0)
        val time3pm = LocalTime.of(15, 0)

        // Weekly
        assertEquals(
            "Every Thursday at 2 pm",
            describeRecurrence("FREQ=WEEKLY;BYDAY=TH", thursday, time2pm, is24Hour = false)
        )
        assertEquals(
            "Every Thursday at 14:00",
            describeRecurrence("FREQ=WEEKLY;BYDAY=TH", thursday, time2pm, is24Hour = true)
        )
        assertEquals(
            "Every 2 weeks on Thursday at 2 pm",
            describeRecurrence("FREQ=WEEKLY;INTERVAL=2;BYDAY=TH", thursday, time2pm, is24Hour = false)
        )
        assertEquals(
            "Every weekday at 9 am",
            describeRecurrence("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", thursday, LocalTime.of(9, 0), is24Hour = false)
        )

        // Yearly
        assertEquals(
            "Every 8 October at 3 pm",
            describeRecurrence("FREQ=YEARLY", thursday, time3pm, is24Hour = false)
        )
        assertEquals(
            "Every 8 October at 15:00",
            describeRecurrence("FREQ=YEARLY", thursday, time3pm, is24Hour = true)
        )

        // Monthly
        assertEquals(
            "Every month on the 8th at 3 pm",
            describeRecurrence("FREQ=MONTHLY", thursday, time3pm, is24Hour = false)
        )
        assertEquals(
            "Every month on the 8th at 15:00",
            describeRecurrence("FREQ=MONTHLY", thursday, time3pm, is24Hour = true)
        )

        // Daily
        assertEquals(
            "Every day at 8 am",
            describeRecurrence("FREQ=DAILY", thursday, LocalTime.of(8, 0), is24Hour = false)
        )
        assertEquals(
            "Every 3 days at 8:30 am",
            describeRecurrence("FREQ=DAILY;INTERVAL=3", thursday, LocalTime.of(8, 30), is24Hour = false)
        )

        // Hourly
        assertEquals(
            "Every hour",
            describeRecurrence("FREQ=HOURLY", thursday, time2pm, is24Hour = false)
        )
        assertEquals(
            "Every 2 hours",
            describeRecurrence("FREQ=HOURLY;INTERVAL=2", thursday, time2pm, is24Hour = false)
        )
    }

    @Test
    fun `describes recurrence from dueAt string`() {
        assertEquals(
            "Every Thursday at 2 pm",
            describeRecurrence(
                rrule = "FREQ=WEEKLY;BYDAY=TH",
                dueAt = "2026-10-08T14:00:00Z",
                is24Hour = false,
                zone = java.time.ZoneOffset.UTC
            )
        )
    }

    @Test
    fun `deserializes reminder occurrence response`() {
        val json = """
            {
                "id": 42,
                "reminderId": "rem_123",
                "occursAt": "2026-10-08T14:00:00Z",
                "status": "completed",
                "completedAt": "2026-10-08T14:05:00Z",
                "customBody": "Specific meeting topic",
                "effectiveBody": "Specific meeting topic"
            }
        """.trimIndent()
        val parsed = Json { ignoreUnknownKeys = true }.decodeFromString<ReminderOccurrenceResponse>(json)
        assertEquals(42L, parsed.id)
        assertEquals("rem_123", parsed.reminderId)
        assertEquals("completed", parsed.status)
        assertEquals("Specific meeting topic", parsed.customBody)
        assertEquals("Specific meeting topic", parsed.effectiveBody)
    }

    @Test
    fun `serializes update reminder occurrence request`() {
        val req = UpdateReminderOccurrenceRequest(
            occursAt = "2026-10-08T14:00:00Z",
            status = "skipped",
            customBody = "Updated text"
        )
        val json = Json.encodeToString(UpdateReminderOccurrenceRequest.serializer(), req)
        assertTrue(json.contains("\"occursAt\":\"2026-10-08T14:00:00Z\""))
        assertTrue(json.contains("\"status\":\"skipped\""))
        assertTrue(json.contains("\"customBody\":\"Updated text\""))
    }

    @Test
    fun `serializes update reminder request omitting null clearDeviceIds and including true`() {
        val wireJson = Json { explicitNulls = false }

        val regular = uk.co.adamkhattab.pcconnect.data.UpdateReminderRequest(
            body = "Updated body",
            dueAt = "2026-10-08T14:00:00Z",
            timezone = "Europe/London",
            rrule = "",
        )
        val regularPayload = wireJson.encodeToString(
            uk.co.adamkhattab.pcconnect.data.UpdateReminderRequest.serializer(),
            regular,
        )
        assertFalse(regularPayload.contains("clearDeviceIds"))
        assertTrue(regularPayload.contains("\"rrule\":\"\""))

        val clearing = regular.copy(clearDeviceIds = true)
        val clearingPayload = wireJson.encodeToString(
            uk.co.adamkhattab.pcconnect.data.UpdateReminderRequest.serializer(),
            clearing,
        )
        assertTrue(clearingPayload.contains("\"clearDeviceIds\":true"))
    }

    @Test
    fun `active recurring reminders remain visible on device screen even when series is marked completed`() {
        val now = Instant.parse("2026-11-01T15:30:00Z")
        val recurringCompletedToday = Reminder(
            id = "rem-rec-1",
            body = "rhhggd",
            dueAt = "2026-10-28T04:00:00Z",
            dueLocalTime = "04:00",
            timezone = "Europe/London",
            rrule = "FREQ=HOURLY;INTERVAL=21",
            recurrenceUntil = "2027-02-10T23:59:59Z",
            isCompleted = true,
            completedAt = "2026-11-01T04:05:00Z",
            createdAt = "2026-10-28T03:00:00Z",
            updatedAt = "2026-11-01T04:05:00Z",
        )
        assertTrue(recurringCompletedToday.isActiveReminder(now))

        val recurringEnded = recurringCompletedToday.copy(recurrenceUntil = "2026-10-31T23:59:59Z")
        assertFalse(recurringEnded.isActiveReminder(now))

        val oneTimeCompleted = recurringCompletedToday.copy(rrule = null, recurrenceUntil = null, isCompleted = true)
        assertFalse(oneTimeCompleted.isActiveReminder(now))

        val oneTimeOpen = oneTimeCompleted.copy(isCompleted = false)
        assertTrue(oneTimeOpen.isActiveReminder(now))
    }

    @Test
    fun `occurrences are sorted oldest to newest and hourly interval is respected`() {
        val unsorted = listOf(
            ReminderOccurrenceResponse(reminderId = "r1", occursAt = "2026-11-03T07:00:00Z", effectiveBody = "rhhggd"),
            ReminderOccurrenceResponse(reminderId = "r1", occursAt = "2026-10-30T19:00:00Z", effectiveBody = "rhhggd"),
            ReminderOccurrenceResponse(reminderId = "r1", occursAt = "2026-11-01T13:00:00Z", effectiveBody = "rhhggd"),
            ReminderOccurrenceResponse(reminderId = "r1", occursAt = "2026-10-31T16:00:00Z", effectiveBody = "rhhggd"),
            ReminderOccurrenceResponse(reminderId = "r1", occursAt = "2026-11-02T10:00:00Z", effectiveBody = "rhhggd"),
        )

        val sorted = AppViewModel.sortOccurrencesOldestToNewest(unsorted)
        assertEquals(
            listOf(
                "2026-10-30T19:00:00Z",
                "2026-10-31T16:00:00Z",
                "2026-11-01T13:00:00Z",
                "2026-11-02T10:00:00Z",
                "2026-11-03T07:00:00Z",
            ),
            sorted.map { it.occursAt },
        )

        val reminder = Reminder(
            id = "rem-hourly",
            body = "rhhggd",
            dueAt = "2026-10-28T04:00:00Z",
            dueLocalTime = "04:00",
            timezone = "UTC",
            rrule = "FREQ=HOURLY;INTERVAL=21",
            recurrenceUntil = "2027-02-10T23:59:59Z",
            isCompleted = false,
            createdAt = "2026-10-28T03:00:00Z",
            updatedAt = "2026-10-28T03:00:00Z",
        )
        val generated = AppViewModel.generateLocalOccurrencesFor(
            reminder = reminder,
            now = Instant.parse("2026-11-01T18:00:00Z"),
            zone = ZoneId.of("UTC"),
        )
        assertEquals(10, generated.size)
        assertEquals("2026-11-02T10:00:00Z", generated[0].occursAt)
        assertEquals("2026-11-03T07:00:00Z", generated[1].occursAt)
        assertTrue(
            generated.zipWithNext().all { (older, newer) ->
                Instant.parse(older.occursAt).isBefore(Instant.parse(newer.occursAt))
            },
        )
    }

    @Test
    fun `desktop command preferences apply per PC and take priority over mobile settings`() {
        val adamPc = Device(
            id = "pc-adam",
            displayName = "ADAM-PC",
            platform = "windows",
            osVersion = "Windows 11",
            agentVersion = "2.0.0",
            status = "active",
            isOnline = true,
            pairedAt = "2026-10-01T00:00:00Z",
            allowedCommands = listOf("lock", "sleep", "restart", "shutdown"),
            passwordRequiredCommands = listOf("restart", "shutdown"),
        )
        val laptopPc = adamPc.copy(
            id = "pc-laptop",
            displayName = "LAPTOP",
            allowedCommands = emptyList(),
            passwordRequiredCommands = listOf("shutdown"),
        )

        // Mobile turned off restart and turned on hibernate + lock:
        val mobileProtected = setOf("hibernate", "lock")

        // 1. Available commands strictly follow each desktop's own allowedCommands
        assertEquals(listOf("lock", "sleep", "restart", "shutdown"), CommandPolicy.availableCommands(adamPc))
        assertEquals(CommandTypes.ALL, CommandPolicy.availableCommands(laptopPc))
        assertFalse(CommandPolicy.isCommandAllowed(adamPc, "hibernate"))
        assertTrue(CommandPolicy.isCommandAllowed(laptopPc, "hibernate"))

        // 2. Desktop-disabled command cannot be protected or sent even if mobile protects it
        assertFalse(CommandPolicy.isCommandProtected(adamPc, "hibernate", mobileProtected))

        // 3. Desktop-protected command stays protected on ADAM-PC even when mobile turned restart OFF
        assertTrue(CommandPolicy.isCommandProtected(adamPc, "restart", mobileProtected))
        // And ADAM-PC's restart requirement does NOT leak onto LAPTOP
        assertFalse(CommandPolicy.isCommandProtected(laptopPc, "restart", mobileProtected))

        // 4. Mobile can still add phone-side confirmation for an allowed, desktop-unprotected command
        assertTrue(CommandPolicy.isCommandProtected(adamPc, "lock", mobileProtected))
        assertFalse(CommandPolicy.isCommandProtected(adamPc, "sleep", mobileProtected))
    }
}
