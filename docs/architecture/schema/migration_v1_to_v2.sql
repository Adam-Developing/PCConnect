-- =============================================================================
-- PCConnect — v1 to v2 migration, expand/contract sequence
--
-- This file is the REFERENCE for the migration. In the repository it is split
-- into one dbmate migration per numbered step under db/migrations/, so that
-- each can be applied, verified and rolled back independently.
--
--   Stage        Steps      Reversible?   Gate to advance (02-data-architecture.md §5)
--   EXPAND       E1..E6     yes           objects exist in staging + prod, unread
--   DUAL WRITE   (app)      yes           48h, zero divergence on V5
--   BACKFILL     B1..B7     yes           V1..V5 return zero for 24h
--   CUTOVER      (app)      yes           72h staging + 24h prod canary
--   CONTRACT     C1..C6     NO            legacy traffic < 1% for 14 days
--
-- Every statement assumes: SET time_zone = '+00:00';
-- =============================================================================

SET NAMES utf8mb4;
SET time_zone = '+00:00';


-- #############################################################################
-- STAGE 1 — EXPAND   (additive only; nothing reads these yet)
-- #############################################################################

-- ---------------------------------------------------------------- E1 --------
-- Public ids on the existing tables. Nullable during expand so the backfill
-- can populate them without a lock-the-world UPDATE.
ALTER TABLE users
  ADD COLUMN public_id BINARY(16) NULL AFTER id,
  ADD COLUMN email_normalised VARCHAR(320) NULL,
  ADD COLUMN username_normalised VARCHAR(64) NULL,
  ADD COLUMN timezone VARCHAR(64) NOT NULL DEFAULT 'Europe/London',
  ADD COLUMN locale VARCHAR(16) NOT NULL DEFAULT 'en-GB',
  ADD COLUMN status VARCHAR(16) NOT NULL DEFAULT 'active',
  ADD COLUMN dek_wrapped VARBINARY(255) NULL,
  ADD COLUMN dek_kek_id VARCHAR(32) NULL,
  ADD COLUMN created_at DATETIME(3) NULL,
  ADD COLUMN updated_at DATETIME(3) NULL,
  ADD COLUMN deleted_at DATETIME(3) NULL;

-- 'Europe/London' as the default is a deliberate, documented assumption: the
-- product originated in the UK and pre-migration reminders were already being
-- interpreted that way. B4 refines it per user where evidence exists.

-- ---------------------------------------------------------------- E2 --------
-- Credentials move out of `users`. algo starts as legacy for every row; the
-- upgrade-on-login path (02 §6) converts them individually.
CREATE TABLE user_credentials (
  user_id             BIGINT UNSIGNED NOT NULL,
  algo                VARCHAR(24) NOT NULL DEFAULT 'legacy_sha256_unsalted',
  password_hash       VARBINARY(255) NOT NULL,
  must_rehash         TINYINT(1) UNSIGNED NOT NULL DEFAULT 1,
  password_changed_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  failed_attempts     INT UNSIGNED NOT NULL DEFAULT 0,
  locked_until        DATETIME(3) NULL,
  created_at          DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  updated_at          DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
  PRIMARY KEY (user_id),
  CONSTRAINT fk_user_credentials_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
  CONSTRAINT ck_user_credentials_algo CHECK (algo IN ('argon2id','legacy_sha256_unsalted'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- ---------------------------------------------------------------- E3 --------
-- Devices. `legacy_pcid` is the bridge back to pcnames and is dropped at C5.
CREATE TABLE devices (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id        BINARY(16) NOT NULL,
  user_id          BIGINT UNSIGNED NOT NULL,
  legacy_pcid      INT NULL COMMENT 'TEMPORARY bridge to pcnames.PCID; dropped at C5',
  display_name     VARCHAR(128) NOT NULL,
  platform         VARCHAR(16) NOT NULL DEFAULT 'windows',
  os_version       VARCHAR(64) NOT NULL DEFAULT '',
  agent_version    VARCHAR(32) NOT NULL DEFAULT '',
  allowed_commands JSON NOT NULL,
  status           VARCHAR(16) NOT NULL DEFAULT 'active',
  paired_at        DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  last_seen_at     DATETIME(3) NULL,
  created_at       DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  updated_at       DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
  revoked_at       DATETIME(3) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_devices_public_id (public_id),
  UNIQUE KEY uq_devices_legacy_pcid (legacy_pcid),
  UNIQUE KEY uq_devices_user_name (user_id, display_name),
  KEY ix_devices_user_status (user_id, status),
  CONSTRAINT fk_devices_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
  CONSTRAINT ck_devices_status CHECK (status IN ('active','revoked','suspended'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- ---------------------------------------------------------------- E4 --------
-- The command lifecycle. See v2_target_schema.sql for the full definitions of
-- commands, command_events, device_credentials, device_pairings,
-- refresh_tokens, auth_challenges, idempotency_keys and security_events.
-- (Elided here to keep this file focused on the *migration*, not the DDL.)

-- ---------------------------------------------------------------- E5 --------
-- Reminders gain the new representation alongside the old columns.
ALTER TABLE reminders
  ADD COLUMN public_id       BINARY(16) NULL,
  ADD COLUMN body_ciphertext VARBINARY(4096) NULL COMMENT 'AES-256-GCM under the per-user DEK',
  ADD COLUMN body_dek_id     VARCHAR(32) NULL,
  ADD COLUMN due_at_utc      DATETIME(3) NULL,
  ADD COLUMN due_local_time  TIME NULL,
  ADD COLUMN timezone        VARCHAR(64) NULL,
  ADD COLUMN rrule           VARCHAR(255) NULL,
  ADD COLUMN is_completed    TINYINT(1) UNSIGNED NOT NULL DEFAULT 0,
  ADD COLUMN completed_at    DATETIME(3) NULL,
  ADD COLUMN created_at      DATETIME(3) NULL,
  ADD COLUMN updated_at      DATETIME(3) NULL,
  ADD COLUMN deleted_at      DATETIME(3) NULL;

-- ---------------------------------------------------------------- E6 --------
CREATE TABLE nav_items (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  label       VARCHAR(128) NOT NULL,
  url         VARCHAR(512) NOT NULL,
  placement   VARCHAR(16) NOT NULL,
  sort_order  INT NOT NULL DEFAULT 0,
  is_external TINYINT(1) UNSIGNED NOT NULL DEFAULT 0,
  is_visible  TINYINT(1) UNSIGNED NOT NULL DEFAULT 1,
  PRIMARY KEY (id),
  KEY ix_nav_items_placement (placement, is_visible, sort_order),
  CONSTRAINT ck_nav_items_placement CHECK (placement IN ('header','footer','both'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- #############################################################################
-- STAGE 2 — DUAL WRITE  (application change; no DDL)
--
-- The API writes both shapes for every mutation:
--   command issue  -> UPDATE pcnames SET Value/Request  AND  INSERT commands
--   heartbeat      -> UPDATE pcnames.Time               AND  UPDATE devices.last_seen_at
--   reminder write -> legacy Reminder column            AND  body_ciphertext
--
-- Verification query V5 (02 §5.1) runs on a schedule throughout and must stay
-- at zero for 48 hours before the backfill starts.
-- #############################################################################


-- #############################################################################
-- STAGE 3 — BACKFILL  (idempotent, resumable, batched)
--
-- Every step is written so it can be re-run safely after an interruption.
-- Batch size 1000; a bounded transaction per batch keeps the undo log small
-- and lets the job be killed at any point without a long rollback.
-- #############################################################################

-- ---------------------------------------------------------------- B1 --------
-- Public ids. UUID_TO_BIN(UUID(), 1) is byte-swapped so the time component
-- leads, keeping the secondary index roughly append-ordered.
-- (Production uses application-generated UUIDv7; UUIDv1 here keeps the
--  reference runnable in plain MySQL.)
UPDATE users     SET public_id = UUID_TO_BIN(UUID(), 1) WHERE public_id IS NULL LIMIT 1000;
UPDATE reminders SET public_id = UUID_TO_BIN(UUID(), 1) WHERE public_id IS NULL LIMIT 1000;

-- ---------------------------------------------------------------- B2 --------
-- Normalised uniqueness keys. Run the duplicate check FIRST — adding the
-- unique index at C2 will fail if these return rows, and it is far better to
-- discover that now than during the contract window.
SELECT email_normalised, COUNT(*) AS n
FROM (SELECT LOWER(TRIM(Email)) AS email_normalised FROM users) t
GROUP BY email_normalised HAVING n > 1;          -- expect: empty

SELECT username_normalised, COUNT(*) AS n
FROM (SELECT LOWER(TRIM(Username)) AS username_normalised FROM users) t
GROUP BY username_normalised HAVING n > 1;       -- expect: empty

UPDATE users
SET email_normalised    = LOWER(TRIM(Email)),
    username_normalised = LOWER(TRIM(Username)),
    status              = IF(Enabled = 1, 'active', 'suspended'),
    created_at          = COALESCE(
                            STR_TO_DATE(DateTimeOfSignup, '%Y-%m-%d %H:%i:%s'),
                            CURRENT_TIMESTAMP(3)),
    updated_at          = CURRENT_TIMESTAMP(3)
WHERE email_normalised IS NULL;

-- ---------------------------------------------------------------- B3 --------
-- Credentials. INSERT IGNORE makes the step idempotent.
INSERT IGNORE INTO user_credentials (user_id, algo, password_hash, must_rehash)
SELECT id, 'legacy_sha256_unsalted', CAST(Password AS BINARY), 1
FROM users;

-- ---------------------------------------------------------------- B4 --------
-- Timezone, refined from evidence. `verifications.Current` holds the IANA zone
-- captured at password reset for some users; it is better than the default.
UPDATE users u
JOIN (
  SELECT UserID, MAX(ID) AS latest
  FROM verifications
  WHERE `Current` <> '' AND `Current` LIKE '%/%'
  GROUP BY UserID
) pick ON pick.UserID = u.id
JOIN verifications v ON v.ID = pick.latest
SET u.timezone = v.`Current`
WHERE u.timezone = 'Europe/London';

-- ---------------------------------------------------------------- B5 --------
-- Devices from pcnames. Duplicate display names within one user are
-- disambiguated by PCID so uq_devices_user_name can be created at C2.
INSERT IGNORE INTO devices
  (public_id, user_id, legacy_pcid, display_name, allowed_commands, last_seen_at, paired_at)
SELECT
  UUID_TO_BIN(UUID(), 1),
  p.UserID,
  p.PCID,
  IF(dupe.n > 1, CONCAT(TRIM(p.PCName), ' (', p.PCID, ')'), TRIM(p.PCName)),
  CAST('["shutdown","restart","signout","lock","sleep","hibernate"]' AS JSON),
  STR_TO_DATE(p.Time, '%Y-%m-%d %H:%i:%s'),
  CURRENT_TIMESTAMP(3)
FROM pcnames p
JOIN (
  SELECT UserID, TRIM(PCName) AS nm, COUNT(*) AS n
  FROM pcnames GROUP BY UserID, TRIM(PCName)
) dupe ON dupe.UserID = p.UserID AND dupe.nm = TRIM(p.PCName)
WHERE NOT EXISTS (SELECT 1 FROM devices d WHERE d.legacy_pcid = p.PCID);

-- NOTE: pcnames.Request / pcnames.Value are deliberately NOT migrated.
-- A command pending at cutover is stale by definition (finding S2-03), and
-- carrying it across would execute a power command the user issued hours ago.

-- ---------------------------------------------------------------- B6 --------
-- Reminder times: local wall-clock -> UTC instant, using the user's timezone.
UPDATE reminders r
JOIN users u ON u.id = r.UserID
SET r.due_local_time = r.Time,
    r.timezone       = u.timezone,
    r.due_at_utc     = CONVERT_TZ(TIMESTAMP(r.Date, r.Time), u.timezone, '+00:00'),
    r.is_completed   = IF(r.Completed = 1, 1, 0),
    r.created_at     = COALESCE(r.created_at, CURRENT_TIMESTAMP(3)),
    r.updated_at     = CURRENT_TIMESTAMP(3)
WHERE r.due_at_utc IS NULL AND r.Date IS NOT NULL;

-- CONVERT_TZ returns NULL if the tz tables are not loaded. Load them first:
--   mysql_tzinfo_to_sql /usr/share/zoneinfo | mysql -u root mysql
-- and assert zero NULLs afterwards:
SELECT COUNT(*) AS unconverted FROM reminders WHERE Date IS NOT NULL AND due_at_utc IS NULL;

-- Recurrence -> RRULE. Only the shapes the old columns could express are
-- translated; anything else is logged and left NULL. No client reads these
-- columns today (finding S2-09), so the risk of a miss is nil.
UPDATE reminders
SET rrule = CASE LOWER(COALESCE(Recurrence_Frequency, Recurrence))
              WHEN 'daily'   THEN 'FREQ=DAILY'
              WHEN 'weekly'  THEN CONCAT('FREQ=WEEKLY',
                                     IF(Recurrence_Day IS NULL, '',
                                        CONCAT(';BYDAY=', UPPER(LEFT(Recurrence_Day, 2)))))
              WHEN 'monthly' THEN 'FREQ=MONTHLY'
              WHEN 'yearly'  THEN 'FREQ=YEARLY'
              ELSE NULL
            END
WHERE Recurrence IS NOT NULL AND Recurrence <> 'none' AND rrule IS NULL;

UPDATE reminders
SET rrule = CONCAT(rrule, ';UNTIL=',
                   DATE_FORMAT(Recurrence_End_Date, '%Y%m%dT000000Z'))
WHERE rrule IS NOT NULL AND Recurrence_End_Date IS NOT NULL AND rrule NOT LIKE '%UNTIL=%';

-- ---------------------------------------------------------------- B7 --------
-- Reminder re-encryption is an APPLICATION job, not SQL: it must decrypt with
-- users.api_key (AES-256-CBC) and re-encrypt under the per-user DEK
-- (AES-256-GCM). See 02-data-architecture.md §7. It is resumable by user id
-- and must complete before C4 drops users.api_key.
--
-- Progress:
--   SELECT COUNT(*) FROM reminders WHERE body_ciphertext IS NULL;   -- must reach 0

-- Website navigation: union the two duplicate tables.
INSERT INTO nav_items (label, url, placement, sort_order, is_external)
SELECT Name, URL, 'header', sort_order, URL LIKE 'http%' FROM menupages
UNION
SELECT Name, URL, 'footer', sort_order, URL LIKE 'http%' FROM links
ON DUPLICATE KEY UPDATE sort_order = VALUES(sort_order);


-- #############################################################################
-- STAGE 4 — VERIFY   (all must return 0; see 02 §5.1 for V1-V5)
-- #############################################################################

SELECT
  (SELECT COUNT(*) FROM pcnames p
     LEFT JOIN devices d ON d.legacy_pcid = p.PCID WHERE d.id IS NULL)          AS v1_orphaned_pcs,
  (SELECT COUNT(*) FROM reminders
     WHERE body_ciphertext IS NULL OR OCTET_LENGTH(body_ciphertext) < 29)       AS v2_unmigrated_reminders,
  (SELECT COUNT(*) FROM users u
     LEFT JOIN user_credentials c ON c.user_id = u.id WHERE c.user_id IS NULL)  AS v3_credential_mismatch,
  (SELECT COUNT(*) FROM commands
     WHERE status IN ('issued','delivered') AND expires_at < UTC_TIMESTAMP(3))  AS v4_stale_pending,
  (SELECT COUNT(*) FROM users WHERE public_id IS NULL)                          AS v6_missing_public_id,
  (SELECT COUNT(*) FROM reminders WHERE Date IS NOT NULL AND due_at_utc IS NULL) AS v7_unconverted_times;


-- #############################################################################
-- STAGE 5 — CONTRACT   ***ONE-WAY DOOR***
--
-- Do not run any of this until:
--   * V1-V7 have been zero for 7 consecutive days after cutover
--   * legacy client traffic is under 1% for 14 days (ADR-0008)
--   * a verified restore of the pre-contract backup exists
-- #############################################################################

-- ---------------------------------------------------------------- C1 --------
-- Charset conversion, smallest tables first, in a low-traffic window.
ALTER TABLE nav_items  CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
ALTER TABLE feedback   CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
ALTER TABLE devices    CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
ALTER TABLE users      CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
ALTER TABLE reminders  CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- ---------------------------------------------------------------- C2 --------
-- Tighten what the backfill has now guaranteed.
ALTER TABLE users
  MODIFY public_id BINARY(16) NOT NULL,
  MODIFY email_normalised VARCHAR(320) NOT NULL,
  MODIFY username_normalised VARCHAR(64) NOT NULL,
  MODIFY created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  ADD UNIQUE KEY uq_users_public_id (public_id),
  ADD UNIQUE KEY uq_users_email (email_normalised),
  ADD UNIQUE KEY uq_users_username (username_normalised),
  ADD CONSTRAINT ck_users_status CHECK (status IN ('active','suspended','pending_verification'));

ALTER TABLE reminders
  MODIFY public_id BINARY(16) NOT NULL,
  MODIFY due_at_utc DATETIME(3) NOT NULL,
  MODIFY timezone VARCHAR(64) NOT NULL,
  ADD UNIQUE KEY uq_reminders_public_id (public_id),
  ADD KEY ix_reminders_user_due (UserID, is_completed, due_at_utc),
  ADD KEY ix_reminders_due_sweep (due_at_utc, is_completed, deleted_at);

-- ---------------------------------------------------------------- C3 --------
ALTER TABLE reminders
  MODIFY body_ciphertext VARBINARY(4096) NOT NULL,
  MODIFY body_dek_id VARCHAR(32) NOT NULL,
  DROP COLUMN Reminder,
  DROP COLUMN Completed,
  DROP COLUMN Recurrence,
  DROP COLUMN Recurrence_Frequency,
  DROP COLUMN Recurrence_Day,
  DROP COLUMN Recurrence_Time,
  DROP COLUMN Recurrence_End_Date;

-- ---------------------------------------------------------------- C4 --------
-- ***THE GUARD.*** users.api_key is the ONLY key that can decrypt the legacy
-- reminder column. Dropping it before B7 completes destroys every reminder
-- irrecoverably. This SIGNAL aborts the migration rather than proceeding.
SET @unmigrated := (SELECT COUNT(*) FROM reminders WHERE body_ciphertext IS NULL);
SET @msg := CONCAT('ABORT: ', @unmigrated, ' reminders not yet re-encrypted. See 02 §7.');

DELIMITER //
CREATE PROCEDURE assert_reminders_migrated()
BEGIN
  IF (SELECT COUNT(*) FROM reminders WHERE body_ciphertext IS NULL) > 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ABORT: reminders not re-encrypted; refusing to drop api_key';
  END IF;
END //
DELIMITER ;

CALL assert_reminders_migrated();
DROP PROCEDURE assert_reminders_migrated;

ALTER TABLE users
  DROP COLUMN api_key,
  DROP COLUMN Password,
  DROP COLUMN DateOfBirth,     -- collected, never read; removing it shrinks the PII surface
  DROP COLUMN Enabled,
  DROP COLUMN DateTimeOfSignup;

-- ---------------------------------------------------------------- C5 --------
ALTER TABLE devices DROP COLUMN legacy_pcid;

-- ---------------------------------------------------------------- C6 --------
-- Dead tables. Take a final dump of each before dropping.
DROP TABLE IF EXISTS pcnames;            -- -> devices + commands
DROP TABLE IF EXISTS requests;           -- dead since the mailbox moved onto pcnames
DROP TABLE IF EXISTS `time`;             -- dead; -> devices.last_seen_at
DROP TABLE IF EXISTS apikeys;            -- orphaned; rows have empty usernames
DROP TABLE IF EXISTS `code`;             -- three rows reading 'INSERT CODE HERE FROM THE CODE OUTPUT'
DROP TABLE IF EXISTS links;              -- -> nav_items
DROP TABLE IF EXISTS menupages;          -- -> nav_items
DROP TABLE IF EXISTS verifications;      -- -> auth_challenges (codes deliberately not carried across)
DROP TABLE IF EXISTS verificationtypes;  -- -> auth_challenges.purpose CHECK


-- #############################################################################
-- ROLLBACK NOTES
--
--   E1-E6, B1-B7   revert by reverting the application; new objects are unread
--                  and can be left in place or dropped.
--   C1-C6          NOT reversible. Recovery is a restore from the pre-contract
--                  backup, which must be verified before C1 begins.
--
-- This asymmetry is why CONTRACT is last, is a separate approval, and is gated
-- on measured client traffic rather than on a date.
-- #############################################################################
