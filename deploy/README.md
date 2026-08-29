# PCConnect v2 deployment

This directory defines the local/staging-validated deployment procedure for the Oracle VPS. It does not contain credentials and no production action is automatic.

1. Copy `config.example.env` outside the repository, set it to `staging`, and keep it root-owned mode `600`.
2. Create each file named by `compose.yaml` under `PCCONNECT_SECRET_ROOT`, mode `400` or `600`. Files mounted into the non-root .NET services must be owned by numeric UID `1654`; `preflight.sh` checks this because local Compose file-backed secrets cannot remap ownership. Keep `postgres_password` root-owned for the PostgreSQL entrypoint. Cryptographic keys are independent 32-byte random values encoded as Base64. The PostgreSQL connection secret is a complete Npgsql connection string. The data-protection PFX and its password are distinct from Windows code-signing material.
3. Pre-create every state subdirectory named by `compose.yaml`; make `exports` and `data-protection` owned by container UID `1654`, and `prometheus` owned by UID `65532`. Keep database and Caddy state root-owned according to their image UIDs. The preflight refuses ambiguous or unreadable ownership instead of changing it automatically.
4. Run `./preflight.sh /root/pcconnect-staging.env`, then `./deploy-staging.sh /root/pcconnect-staging.env`.
5. Preserve the staging smoke output, migration dry-run/apply logs, image digests, SBOMs, backup-restore evidence, and acceptance test run as release evidence.

`verify-staging.sh` is fail-closed unless `PCCONNECT_ENVIRONMENT=staging`. In
addition to readiness and authentication, it checks HTTP-to-HTTPS redirect,
HSTS, CSP, framing/referrer/nosniff headers, hostile-origin CORS denial, the
one-megabyte request-body limit, and absence of common exception leakage. It is
not a production probe.

All Compose images are required to use immutable `@sha256:` references. The
release workflow emits application image digests; platform image digests must be
verified from their signed upstream release before they are copied into the
root-owned environment file.

The protected `release` environment must also define digest-pinned
`PCCONNECT_DOTNET_SDK_IMAGE`, `PCCONNECT_DOTNET_ASPNET_IMAGE`, and
`PCCONNECT_DOTNET_RUNTIME_IMAGE` variables. CI may build exact version tags for
fast feedback, but a release refuses to build application images unless every
base stage is immutable.

Android passkeys require the RP host to publish Digital Asset Links and the API
to accept the signing-certificate-derived native origin. Set
`PCCONNECT_ANDROID_PACKAGE_NAME`, the uppercase colon-delimited certificate
SHA-256 fingerprint in `PCCONNECT_ANDROID_CERT_SHA256`, and
`PCCONNECT_ANDROID_WEBAUTHN_ORIGIN=android:apk-key-hash:<base64url SHA-256 DER
certificate>`. These are public identifiers, not private signing material. The
protected release workflow derives both values from the actual release
certificate and refuses a mismatch. Staging must use the package/fingerprint of
the staging build; production must use the existing approved signing lineage.
`PCCONNECT_ANDROID_API_BASE_URL` selects an HTTPS `/api/v2/` endpoint at build
time so staging binaries never need a source edit. Set
`PCCONNECT_ANDROID_RP_HOST` to the same value as `PCCONNECT_WEBAUTHN_RP_ID` so
verified email/reset App Links and passkey association share the authoritative
RP host.

## Backup and restore rehearsal

`backup-base.sh` streams a PostgreSQL base backup and a separate recovery-key
archive through `age`, uploads both with `rclone`, and verifies checksums.
`archive-wal.sh` encrypts and uploads completed WAL segments every five minutes;
plaintext archive copies are removed only after encrypted remote verification
and a cumulative, keyed deletion-tombstone ledger is exported at the same
interval. The ledger has no user IDs and is replayed after PITR by recomputing
digests with the restored escrow key, preventing resurrection when the chosen
recovery target predates an account deletion. Install the supplied systemd services/timers
only after reviewing their environment-file paths. A production environment is
refused until `PCCONNECT_PRODUCTION_BACKUP_APPROVED=I_HAVE_EXPLICIT_APPROVAL` is
recorded following explicit approval.

Run `restore-rehearsal.sh` only with a staging environment, an unused path below
`/srv/pcconnect-v2/restore-rehearsals`, an approved escrow identity, a base-backup
ID, and an ISO-8601 UTC recovery target. It decrypts the database and key archive,
requests WAL recovery in a network-isolated PostgreSQL container, checks database
integrity counts, and records measured recovery duration. It never connects to or
overwrites the active database.

`deploy-production.sh` intentionally stops after the approval gates. Production migration needs a separate explicit user approval, a change ticket, a fresh encrypted backup, successful staging evidence, and an agreed rollback window. The old API slot remains available after a switch. Schema changes must be expand-compatible with it.

Only ports 80/443 are published. PostgreSQL and Valkey are on an internal network. App secrets are file-mounted; the API/worker read them through the .NET key-per-file provider. The API instances share a certificate-protected persistent ASP.NET Data Protection key ring.
