# PCConnect — Operations Runbook

Deploy, roll back, restore, rotate keys, respond to an incident.

Required by [07 §4](architecture/07-migration-plan.md). Everything here has been run at least once
against a local stack; the places where it has *not* been run against production are marked
**UNREHEARSED** rather than left to sound confident.

Every command assumes you are in `deploy/` on the host, with a filled-in `.env` beside
`docker-compose.yml`.

```bash
cd /opt/pcconnect/deploy
```

---

## 0. The shape of the thing

| Service | What it is | Reachable from |
|---|---|---|
| `caddy` | TLS edge, automatic certificates | the internet, ports 80/443 |
| `api` | ASP.NET Core API + SignalR hub | caddy only |
| `worker` | Command expiry sweep, reminder materialisation and delivery | nothing |
| `migrate` | One-shot schema migration; exits | run by hand |
| `postgres` | The database | api, worker, backup |
| `valkey` | SignalR backplane, presence, rate limits | api, worker |
| `backup` | Nightly `pg_dump`, `age`-encrypted | postgres |

`postgres` and `valkey` sit on an `internal: true` network and publish no ports. There is no way
to reach the database from outside the host, by design — the v1 system's database was reachable
with credentials from a file in the web root.

Health: `GET /healthz` (process is up), `GET /readyz` (database and cache answer),
`GET /metrics` (Prometheus). All three answer `404` to anything outside a private range —
`/metrics` in particular leaks per-endpoint volumes — so check them from the host, not from
your laptop.

---

## 1. Deploy

CI builds and pushes images tagged with the commit SHA. A deploy is a change to two lines of
`.env` and a `docker compose up`.

```bash
# 1. Pin the images. Never deploy :latest — you cannot roll back to a tag that moved.
sed -i "s|^PCCONNECT_IMAGE_API=.*|PCCONNECT_IMAGE_API=ghcr.io/adamkhattab/pcconnect-api:$SHA|" .env
sed -i "s|^PCCONNECT_IMAGE_WORKER=.*|PCCONNECT_IMAGE_WORKER=ghcr.io/adamkhattab/pcconnect-worker:$SHA|" .env

# 2. Take a backup first. The nightly one may be 23 hours old. The backup
#    container dumps once at start-up, so restarting it is how you ask for one.
docker compose restart backup
sleep 60 && docker compose exec backup ls -lt /var/backups/pcconnect | head -3

# 3. Migrate. This runs to completion and exits; the API is still serving the old schema.
docker compose run --rm migrate

# 4. Roll the services.
docker compose up -d api worker

# 5. Watch.
docker compose logs -f --tail=50 api
docker compose exec -T api wget -qO- http://localhost:8080/readyz && echo OK
```

**Migrations are expand-only in the same deploy as the code that needs them.** A migration that
drops or narrows a column goes in a *later* deploy, after the code that stopped using it is live.
`pcconnect-migrate up` refuses a destructive migration unless given `--allow-destructive`, which
is the guard for exactly this — see `db/migrations/0007_contract_drop_legacy_bridge.sql`, which
additionally refuses to run while any legacy row is unmigrated.

### Verify a deploy

```bash
curl -s https://$PCCONNECT_DOMAIN/v2/meta/discovery | jq '{version, capabilities}'
docker compose exec -T postgres psql -U pcconnect -d pcconnect -c "select count(*) from users"
```

Then send one `lock` from your own phone to your own PC and watch it reach `succeeded`. A deploy
that passes health checks and cannot execute a command has not been verified.

---

## 2. Roll back

Two different failures, two different procedures. Decide which one you have before typing.

### 2.1 The code is bad, the schema is fine

This is the common case and it is fast, because migrations are expand-only: the previous image
still understands the current schema.

```bash
sed -i "s|^PCCONNECT_IMAGE_API=.*|PCCONNECT_IMAGE_API=ghcr.io/adamkhattab/pcconnect-api:$PREVIOUS_SHA|" .env
sed -i "s|^PCCONNECT_IMAGE_WORKER=.*|PCCONNECT_IMAGE_WORKER=ghcr.io/adamkhattab/pcconnect-worker:$PREVIOUS_SHA|" .env
docker compose up -d api worker
```

Do **not** run `pcconnect-migrate down` as part of this. The old code does not need the schema
reverted, and reverting it is the only irreversible thing in the procedure.

### 2.2 The migration is bad

```bash
docker compose stop api worker          # stop writers first
docker compose run --rm --entrypoint "dotnet pcconnect-migrate.dll" migrate down
docker compose up -d api worker
```

`down` reverts exactly one migration, the most recent. If the bad migration dropped data, `down`
does not bring it back — go to §3.

---

## 3. Restore

### 3.1 Rehearsal (monthly, and before any cutover)

This is the one that must be run when nothing is wrong.

```bash
docker compose exec -T postgres psql -U pcconnect -c "DROP DATABASE IF EXISTS scratch"
docker compose exec -T postgres psql -U pcconnect -c "CREATE DATABASE scratch"

BACKUP_LOCAL_DIR=/var/backups/pcconnect \
BACKUP_AGE_KEY="$(cat ~/pcconnect-backup.age.key)" \
PCCONNECT_DATABASE__CONNECTIONSTRING="Host=postgres;Port=5432;Database=scratch;Username=pcconnect;Password=$POSTGRES_PASSWORD" \
  ./tools/restore-rehearsal.sh

PCCONNECT_DATABASE__CONNECTIONSTRING="…Database=scratch…" \
  docker compose run --rm --entrypoint "dotnet pcconnect-migrate.dll" migrate verify
```

The script refuses to restore into a database called `pcconnect`, `prod` or `production`. That
refusal is the guard, not the naming convention.

CI runs the same script monthly (`.github/workflows/ci.yml`, job `restore-rehearsal`).
**UNREHEARSED in CI**: it needs `BACKUP_AGE_KEY` as a repository secret and a way to reach the
backups, which are on the host and nothing pushes off it yet — see §7.

### 3.2 Real restore

```bash
docker compose stop api worker backup            # nothing may write during a restore
docker compose exec -T postgres psql -U pcconnect -c \
  "ALTER DATABASE pcconnect RENAME TO pcconnect_broken_$(date -u +%Y%m%d)"
docker compose exec -T postgres psql -U pcconnect -c "CREATE DATABASE pcconnect"
```

Keep the broken database. It is evidence, and it is the only copy of anything written since the
backup. Then decrypt and load:

```bash
age --decrypt --identity ~/pcconnect-backup.age.key \
    /var/backups/pcconnect/pcconnect-<stamp>.sql.age \
  | docker compose exec -T postgres psql -U pcconnect -d pcconnect

docker compose run --rm --entrypoint "dotnet pcconnect-migrate.dll" migrate verify
docker compose up -d api worker backup
```

**After any restore, everyone is signed out on purpose.** Refresh tokens issued after the backup
was taken are not in the restored database, and a client presenting one gets a reuse-detection
response. This is correct and must not be "fixed" by relaxing reuse detection.

**RPO is 24 hours** — one nightly dump, no WAL archiving. Anything written since the last dump is
gone. If that is not acceptable, WAL shipping is the fix and is not built (§7).

---

## 4. Rotate the token signing key

Access tokens are ES256 JWTs valid for 15 minutes. Refresh tokens are opaque database rows and
are **not** affected by this rotation.

```bash
openssl ecparam -name prime256v1 -genkey -noout -out jwt-new.pem
# Paste into .env as JWT_PRIVATE_KEY_PEM (generate-secrets.sh does the escaping)
docker compose up -d api
```

Every access token minted by the old key is rejected from the moment the API restarts. Clients
refresh automatically and recover within seconds; the socket reconnects and the catch-up read
brings the client back into step. Expect one spike of `/v2/auth/refresh` traffic.

Rotate this if the key may have been exposed. There is no reason to rotate it on a schedule —
15-minute tokens make the exposure window small on their own.

---

## 5. Rotate the key encryption key

The KEK wraps every user's data key. It is **not** in the database, and losing it loses every
reminder — there is no recovery path and no support ticket that can undo it.

There are two key slots, `KEK_KEY_K1` and `KEK_KEY_K2`, and `KEK_CURRENT_ID` says which one is
current. **The slot name is the id written to `users.dek_kek_id`**, so a rotation fills the empty
slot rather than overwriting the full one — refilling a slot would tell the server that rows
wrapped with the old key were wrapped with the new one, and they would stop decrypting with no
error to explain it.

Rotation is four steps, and the last two are the ones that get skipped.

```bash
# 1. Fill the empty slot and make it current. Both keys stay configured.
openssl rand -base64 32
# .env:  KEK_KEY_K2=<the new key>      (K1 keeps the old one, untouched)
#        KEK_CURRENT_ID=k2
docker compose up -d api worker

# 2. Confirm the split before changing anything else.
docker compose run --rm --entrypoint "dotnet pcconnect-migrate.dll" migrate rewrap-deks --status
#   → k1=812 k2=3 — 812 still on an older KEK. Keep the previous key configured.

# 3. Rewrap. Safe while the API is serving; safe to interrupt; safe to run twice.
docker compose run --rm --entrypoint "dotnet pcconnect-migrate.dll" migrate rewrap-deks
#   → Rewrapped 812 data key(s). k2=815 — every data key is under 'k2'.
#     The previous KEK can be removed from the environment.

# 4. Only now: empty the old slot and destroy the old key.
# .env:  KEK_KEY_K1=
docker compose up -d api worker
```

The next rotation goes the other way — new key into `KEK_KEY_K1`, `KEK_CURRENT_ID=k1` — so the two
slots alternate and an id is never reused for a different key.

Step 4 waits on step 3's last line and nothing else. The rewrap changes the *wrapper* only: the data key underneath is unchanged, so no reminder is
re-encrypted and an interrupted run just leaves some users on each key — a state the system
already serves correctly.

A data key whose KEK is not configured is skipped, counted and named rather than re-keyed:

```
Rewrapped 806 data key(s). import=6 k2=812 — 6 still on an older KEK. Keep the previous key
configured. 6 of them are wrapped with 'import', which is not configured — restore it.
```

That is a finished rotation with six stranded users, not a stalled one. Those six cannot read
their own reminders either, so restoring that key is the actual task; the rotation is not what is
blocked. Put the key back, run it again, and the line changes to "can be removed".

Rehearsed end to end against a local stack: reminder written under `k1`, rewrapped, read back with
only `k2` configured. **UNREHEARSED against production data volumes.**

---

## 6. Incident response

### 6.1 A device is compromised, or someone lost a laptop

```bash
# From the owner's phone: My PCs → the bin icon. Or:
curl -X DELETE https://$PCCONNECT_DOMAIN/v2/devices/$DEVICE_ID -H "Authorization: Bearer $TOKEN"
```

Revoking a device revokes its credential, kills its sessions, and cancels every command still in
flight for it. The agent notices, clears its stored secret and stops trying. Nothing has to be
done on the machine itself.

### 6.2 An account is compromised

```bash
# The user, from any signed-in client:
curl -X POST https://$PCCONNECT_DOMAIN/v2/auth/logout-all -H "Authorization: Bearer $TOKEN"
```

That revokes every refresh-token family, which signs out every client including the agents.
Follow with a password change; changing the password does not on its own revoke sessions, which
is why the order matters.

### 6.3 Something is wrong and you do not yet know what

```bash
docker compose logs --since 30m api | grep -E '"@l":"(Error|Fatal)"'

docker compose exec -T postgres psql -U pcconnect -d pcconnect -c \
  "select event, count(*) from security_events where occurred_at > now() - interval '1 hour'
    group by 1 order by 2 desc limit 20"

docker compose exec -T api wget -qO- http://localhost:8080/metrics | grep pcconnect_command
```

`pcconnect_command_stale_executions_total` leaving zero means an agent executed a command whose
TTL had already passed. That is the one metric that should page: it means a computer could be
powered off by a command that should have been dead. Find the device, revoke it, then find out
why its clock or its TTL check is wrong.

### 6.4 Turn the legacy shim off

```bash
# .env
LEGACY_SHIM_RETIRED=true
docker compose up -d api
```

Every `/api/*` and `/legacy/*` route then answers `410 Gone`. This is a configuration change, not
a deploy — reversible in seconds by setting it back. Use it if the shim is being abused; use it
permanently only when `pcconnect_legacy_requests_total` has been flat at zero for 30 days.

### 6.5 Stop everything

```bash
docker compose stop api worker      # keep postgres up so nothing is lost
```

Caddy keeps answering and returns 502. That is a better failure than a half-serving API: no
command is accepted that cannot be delivered.

---

## 7. What is not covered here, and why

| Gap | Consequence | What it needs |
|---|---|---|
| Backups are not pushed off the host | A host loss loses the backups with it | An off-host target and a key that can write to it |
| No WAL archiving | RPO is 24 h, not 5 min | `archive_command` and somewhere to ship to |
| CI restore rehearsal cannot reach the backups | The monthly job warns and exits | The off-host target above, or an SSH key CI can hold |
| No staging environment | Deploys are rehearsed locally, not on a copy of production | A second host or compose project |
| Production has never been migrated from v1 | The import is exercised against a schema fixture and a local MySQL, not the real data | Access to a production dump, in a controlled window |

None of these are hidden by the procedures above. Where a step has never been run against
production, it says so.

---

Related: [07 — Migration Plan](architecture/07-migration-plan.md) ·
[08 — Platform & Delivery](architecture/08-platform-and-delivery.md) ·
[09 — Implementation Notes](architecture/09-implementation-notes.md)
