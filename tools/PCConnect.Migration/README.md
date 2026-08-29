# PCConnect migration tool

The importer consumes an access-controlled JSON snapshot produced from a
read-only live-database inventory. It never connects to or mutates the legacy
source. `dry_run` is the default rehearsal mode; `full` and `delta` require an
explicit target connection in `PCCONNECT_MIGRATION_TARGET`.

Secrets are accepted only through process environment variables:

- `PCCONNECT_MIGRATION_CHECKSUM_KEY` — Base64 32-byte key used only for
  source-row and manifest reconciliation checksums.
- `PCCONNECT_LEGACY_CREDENTIAL_HASHING_KEY` — Base64 32-byte key used to HMAC
  imported legacy API keys. It must be supplied to the v2 API as
  `Security:LegacyCredentialHashingKey`; it is deliberately distinct from the
  reconciliation key.
- `PCCONNECT_MIGRATION_REMINDER_KEY_ID` and
  `PCCONNECT_MIGRATION_REMINDER_KEY` — target reminder wrapping key.

Example (with values supplied by the staging secret store):

```text
dotnet run --project tools/PCConnect.Migration -- --snapshot rehearsal.legacy-snapshot.json --manifest migration-manifest-rehearsal.json --mode dry_run --source-system hosted-v1 --compat-sunset 2026-11-01T00:00:00.0000000+00:00
```

Snapshots contain PII and credential material. They are gitignored, must be
encrypted at rest, and must be destroyed under the migration runbook.
