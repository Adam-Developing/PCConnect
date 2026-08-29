# Staging capacity verification

These k6 tests implement the architecture targets without embedding accounts or
credentials. They fail closed unless both `PCCONNECT_ENVIRONMENT=staging` and
`PCCONNECT_LOAD_APPROVED=STAGING_ONLY` are set. Never point them at production.

- `api-and-commands.js`: 50 sustained API requests/second and 10 idempotent lock
  commands/second. It requires equal JSON arrays of synthetic controller access
  tokens and their owned reminder-capable device IDs.
- `realtime.js`: 1,000 SignalR controller connections, protocol handshakes, and
  REST catch-up after disconnect. Supply at least 1,000 short-lived synthetic
  access tokens through `PCCONNECT_HUB_TOKENS`.
- `auth-pressure.js`: controlled successful Argon2id logins using at least 20
  synthetic accounts supplied through `PCCONNECT_LOAD_IDENTITIES`. Begin at five
  logins/second and increase only while memory, throttling, and database metrics
  remain healthy.

Run these only after the staging smoke suite and backup/restore rehearsal. Store
the k6 JSON summary alongside VPS CPU/RAM, PostgreSQL pool, outbox age, command
latency and dropped-iteration graphs. A run is acceptance evidence only when it
uses the production VPS class and meets the thresholds in
`docs/architecture/operations.md`.
