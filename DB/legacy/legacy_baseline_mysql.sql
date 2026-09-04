-- =============================================================================
-- v1 baseline — the PRODUCTION shape of the legacy MySQL schema
--
-- This is not the committed dump `DB/pcconnect.sql`: that dump is an older
-- generation than the one the deployed code queries (S2-02). It has
-- `pcnames(PCID, Username TEXT, PCName)` and `reminders.Username`, while the
-- live PHP and Node code read `pcnames.UserID/.Request/.Value/.Time`,
-- `reminders.UserID` and `users.api_key`.
--
-- This file reconstructs the shape the *code* queries, which is what the import
-- must handle. It is the fixture the migration tests run against, and it is the
-- reference for the baseline captured from production in Phase 1.
--
-- The importer tolerates both generations: it probes information_schema for
-- each column it needs and falls back to the older shape when a column is
-- absent (see LegacySchemaProbe).
-- =============================================================================

SET NAMES utf8mb3;

CREATE TABLE IF NOT EXISTS `users` (
  `id`               int NOT NULL AUTO_INCREMENT,
  `Name`             text NOT NULL,
  `Username`         varchar(50) NOT NULL,
  `DateOfBirth`      text NOT NULL,
  `Email`            text NOT NULL,
  `Password`         varchar(64) NOT NULL,   -- unsalted SHA-256 hex, hashed by the CLIENT (S1-03)
  `Enabled`          int NOT NULL DEFAULT '1',
  `DateTimeOfSignup` text NOT NULL,
  `MailingList`      int NOT NULL DEFAULT '0',
  `api_key`          varchar(64) DEFAULT NULL, -- permanent bearer token AND the AES key (S1-05, S1-06)
  PRIMARY KEY (`id`),
  UNIQUE KEY `Username` (`Username`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

CREATE TABLE IF NOT EXISTS `pcnames` (
  `PCID`     int NOT NULL AUTO_INCREMENT,
  `UserID`   int NOT NULL,
  `PCName`   text NOT NULL,
  `Request`  varchar(500) DEFAULT NULL,  -- the mutable command mailbox (S2-03, S2-04)
  `Value`    int NOT NULL DEFAULT '0',
  `Time`     datetime DEFAULT NULL,      -- write-hot presence heartbeat
  PRIMARY KEY (`PCID`),
  KEY `UserID` (`UserID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

CREATE TABLE IF NOT EXISTS `reminders` (
  `ID`                   int NOT NULL AUTO_INCREMENT,
  `UserID`               int NOT NULL,
  `Time`                 time NOT NULL,        -- no timezone anywhere (S2-07)
  `Date`                 date DEFAULT NULL,
  `Reminder`             text NOT NULL,        -- AES-256-CBC under users.api_key (S1-06, S1-07)
  `Completed`            int NOT NULL DEFAULT '0',
  `Recurrence`           varchar(255) DEFAULT 'none',
  `Recurrence_Frequency` varchar(255) DEFAULT NULL,
  `Recurrence_Day`       varchar(255) DEFAULT NULL,
  `Recurrence_Time`      time DEFAULT NULL,
  `Recurrence_End_Date`  date DEFAULT NULL,
  PRIMARY KEY (`ID`),
  KEY `UserID` (`UserID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

CREATE TABLE IF NOT EXISTS `verifications` (
  `ID`      int NOT NULL AUTO_INCREMENT,
  `TypeID`  int NOT NULL,
  `Code`    text NOT NULL,          -- plaintext reset codes; not migrated
  `Expiry`  datetime NOT NULL,
  `Current` text NOT NULL,          -- the only evidence of a user's timezone
  `UserID`  int NOT NULL,
  `IP`      text NOT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

CREATE TABLE IF NOT EXISTS `verificationtypes` (
  `ID`   int NOT NULL AUTO_INCREMENT,
  `Type` text NOT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

CREATE TABLE IF NOT EXISTS `feedback` (
  `FeedbackID` int NOT NULL AUTO_INCREMENT,
  `Name`       text NOT NULL,
  `Email`      text NOT NULL,
  `Feedback`   text NOT NULL,
  `Rating`     text NOT NULL,
  `IP`         text NOT NULL,
  PRIMARY KEY (`FeedbackID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

CREATE TABLE IF NOT EXISTS `links` (
  `ID`         int NOT NULL AUTO_INCREMENT,
  `Name`       text NOT NULL,
  `URL`        text NOT NULL,
  `sort_order` int NOT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

CREATE TABLE IF NOT EXISTS `menupages` (
  `ID`         int NOT NULL AUTO_INCREMENT,
  `Name`       text NOT NULL,
  `URL`        text NOT NULL,
  `sort_order` int NOT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

CREATE TABLE IF NOT EXISTS `mailing_list` (
  `ID`     int NOT NULL AUTO_INCREMENT,
  `UserID` int NOT NULL DEFAULT '0',
  `Email`  text NOT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

-- Dead tables, present in production, carried across by nothing:
--   apikeys   orphaned, superseded by users.api_key
--   requests  superseded by pcnames.Request
--   time      superseded by pcnames.Time
--   code      three placeholder rows with no reader
CREATE TABLE IF NOT EXISTS `apikeys` (
  `ID`  int NOT NULL AUTO_INCREMENT,
  `Key` text NOT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;
