package uk.co.adamkhattab.pcconnect

import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import uk.co.adamkhattab.pcconnect.data.CommandTypes
import uk.co.adamkhattab.pcconnect.data.Discovery
import uk.co.adamkhattab.pcconnect.data.FallbackPollingPolicy
import uk.co.adamkhattab.pcconnect.data.PcConnectApi
import uk.co.adamkhattab.pcconnect.data.Reminder
import uk.co.adamkhattab.pcconnect.ui.RepeatKind
import uk.co.adamkhattab.pcconnect.ui.RepeatSpec
import uk.co.adamkhattab.pcconnect.ui.describeRrule
import uk.co.adamkhattab.pcconnect.ui.rruleFor
import uk.co.adamkhattab.pcconnect.ui.scheduleSummary
import java.time.LocalDate
import java.time.LocalTime

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
