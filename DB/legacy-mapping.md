# Legacy-to-v2 data mapping

**Status:** provisional until completed from a read-only inventory of the live
hosted database. The repository dump is stale and provides names only.

The importer must read legacy data through a least-privilege connection, never
write to it, and emit only non-PII counts/checksums in its reconciliation
manifest.

## Entity mapping

| Legacy evidence | V2 destination | Transformation and invariant |
|---|---|---|
| `users.id` | `users.id` through `legacy_id_map` | Generate stable UUIDv7; normalize username/email; preserve display name; collisions require reviewed resolution |
| `users.Username`, `Email`, `Name` | `users.username`, `email`, `display_name` | Trim Unicode whitespace; case-insensitive uniqueness; do not silently merge accounts |
| `users.DateOfBirth` | `users.date_of_birth` | Parse only known date formats; preserve null; quarantine impossible values |
| `users.MailingList` and mailing consent evidence | `users.marketing_opt_in`, `marketing_consent_at` | Preserve opt-in only when consent evidence is sufficient; otherwise import as opted out and retain the discrepancy in the non-PII manifest |
| `users.Password` | `password_credentials.legacy_sha256` | Accept exactly 64 hex characters; never expose or use as a v2 request credential |
| legacy enabled flag | `users.account_state` | Disabled remains disabled; otherwise active unless reset is required |
| signup date/time | `users.created_at` | Parse known formats explicitly; quarantine rather than locale-guess |
| account API key table/column | `legacy_compat_credentials.credential_hash` | HMAC with migration-only server key; route-scope; absolute day-60 expiry; discard plaintext after import |
| `pcnames.PCID` | `devices.id` through `legacy_id_map` | Stable UUIDv7 and owning user FK |
| `pcnames.PCName` | device display and normalized name | Resolve duplicates per owner using reviewed collision manifest |
| legacy time/heartbeat | `devices.last_seen_at` | Parse only recognized format; never import a device as online |
| legacy request/value | command compatibility inspection only | Do not import stale pending commands at cutover; freeze/drain or quarantine them explicitly |
| `reminders.ID` | `reminders.id` through `legacy_id_map` | Stable UUIDv7 and owning user FK |
| reminder text | encrypted v2 reminder fields | Decrypt only through the known legacy method during isolated import, immediately envelope-encrypt, never log plaintext |
| reminder date/time | local start, timezone and first occurrence | Assume `Europe/London` only when no timezone exists; set `timezone_assumed=true` |
| completed flag | occurrence/delivery migration policy | Import completed history as a completed occurrence/delivery where ownership is known; active reminder creates future occurrence(s) |
| recurrence columns if present | `recurrence_rule` | Convert supported values to RFC 5545; quarantine unknown combinations |

## Live discovery worksheet

Before implementing queries, fill and approve this table from the current host:

| Question | Required evidence | Blocking outcome |
|---|---|---|
| Where is the current API key stored? | table/column/type/uniqueness and route consumers | Any ambiguity or multiple active sources |
| What password shapes exist? | grouped length/algorithm metadata, never raw values | Any value not classifiable as expected SHA-256/reset state |
| How are users linked to devices/reminders? | FK or join semantics and orphan counts | Unresolved ownership or non-unique account identifier |
| What date/time formats exist? | grouped format counts and timezone source | Unparseable active reminder without owner decision |
| How is reminder text encrypted? | code/config ownership and test vector using synthetic text | Key/algorithm unavailable or authentication absent |
| Are commands pending at cutover? | count by known command/state, no user data | Nonzero commands without drain/cancel decision |
| Which client versions are active? | anonymous counts by platform/version/last-seen bucket | Upgrade/sunset plan cannot reach active users |
| What has changed since this repository dump? | schema diff and route inventory | Importer based on stale names |

## Deterministic IDs and checksums

- `legacy_id_map` is the sole identity bridge. Its key is source system, entity
  type and stringified legacy primary key.
- Initial IDs are generated once and reused. Do not derive public UUIDs directly
  from email, username or other PII.
- Source row checksums use a canonical, versioned serialization and a keyed HMAC
  where fields include sensitive data. Manifests contain only aggregate mapping
  checksums.
- A delta run updates an entity only when its source checksum changes. Deletions
  require an explicit source tombstone or approved cutover rule.

## Collision and quarantine policy

Every collision/quarantine receives a non-PII reason code and stable case ID.
Approved resolutions are input to later runs, not manual target-database edits.
Examples include duplicate normalized email, duplicate normalized device name,
orphan reminder, invalid verifier, unknown recurrence, invalid ciphertext and
unparseable timestamp.

No production cutover may proceed with an unapproved case affecting an active
account, device or reminder.
