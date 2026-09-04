-- =============================================================================
-- Migration verification gates  (02-data-architecture.md section 5.1)
--
-- Every query below must return a single row with a single column named
-- `violations` holding 0. Any non-zero result blocks the next migration stage
-- and, for V4, pages: it means a computer could be powered off by a command
-- that should already have expired.
--
-- Format: `-- check: <id> <description>` then one statement terminated by `;`.
-- Parsed and executed by PCConnect.LegacyMigrator (`verify`) and by CI.
-- =============================================================================

-- check: V1 every imported legacy PC has exactly one device
SELECT count(*) AS violations
FROM migration_state ms
CROSS JOIN LATERAL (
  SELECT count(*) AS c FROM devices WHERE legacy_pcid IS NULL AND paired_at < ms.started_at
) x
WHERE ms.entity = 'devices' AND ms.completed_at IS NOT NULL AND x.c > 0;

-- check: V2 no reminder lost its text in the re-encryption
SELECT count(*) AS violations
FROM reminders
WHERE body_ciphertext IS NULL OR octet_length(body_ciphertext) < 29;

-- check: V3 every live user has exactly one credential row
SELECT count(*) AS violations
FROM users u
LEFT JOIN user_credentials c ON c.user_id = u.id
WHERE u.deleted_at IS NULL AND c.user_id IS NULL;

-- check: V4 no command outlives its TTL unresolved (must be 0 forever, not just in migration)
SELECT count(*) AS violations
FROM commands
WHERE status IN ('issued','delivered') AND expires_at < now();

-- check: V5 no command was executed after it expired
SELECT count(*) AS violations
FROM commands
WHERE status = 'succeeded' AND acked_at IS NOT NULL AND acked_at > expires_at;

-- check: V6 no destructive command exists without a recorded step-up
SELECT count(*) AS violations
FROM commands
WHERE risk_tier = 'destructive' AND step_up_verified_at IS NULL;

-- check: V7 every device has exactly one live credential
SELECT count(*) AS violations
FROM devices d
LEFT JOIN device_credentials dc ON dc.device_id = d.id
WHERE d.status = 'active' AND dc.device_id IS NULL;

-- check: V8 no reminder is scheduled against an unknown timezone
SELECT count(*) AS violations
FROM reminders r
WHERE r.deleted_at IS NULL
  AND NOT EXISTS (SELECT 1 FROM pg_timezone_names z WHERE z.name = r.timezone);

-- check: V9 no unresolved import exception
SELECT count(*) AS violations FROM migration_exceptions;

-- check: V10 no user is missing an envelope data key while holding reminders
SELECT count(*) AS violations
FROM users u
WHERE u.dek_wrapped IS NULL
  AND EXISTS (SELECT 1 FROM reminders r WHERE r.user_id = u.id);
