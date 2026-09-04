# ADR-0005 — Database engine and migration tooling

**Status:** Accepted
**Date:** 2026-09-02
**Context docs:** [02](../02-data-architecture.md)

## Context

The schema has no migration history. It evolved by hand plus a set of one-off Python scripts
(`DB/refactor_3nf.py`, `remove_userid_3nf.py`, `refactor_backend.py`, `upload_remote.py`) that were
subsequently deleted (S3-02). The consequences are visible:

- The committed dump `DB/pcconnect.sql` describes a **different generation** of the schema than the
  one the application queries — the dump has `pcnames(PCID, Username TEXT, PCName)` while the code
  reads `pcnames.UserID`, `.Request`, `.Value`, `.Time` and `users.api_key` (S2-02).
- There is no way to stand up a matching database, so there is no staging (S3-07).
- Everything is `utf8mb3` (S2-08) and nothing has a timezone (S2-07).

## Decision

**Stay on MySQL 8.4 LTS.** Adopt **`dbmate`** for versioned, plain-SQL, checksummed migrations, with
`db/migrations/00000000000001_baseline.sql` captured from the **live production schema**.

Standardise on `utf8mb4_0900_ai_ci`, `DATETIME(3)` in UTC, `BIGINT UNSIGNED` surrogate keys with
`BINARY(16)` UUIDv7 public ids, and `CHECK` constraints in place of `ENUM`.

## Options considered

### Engine

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **MySQL 8.4 LTS** (chosen) | Already in production with real data; the maintainer knows it; adequate for the workload; LTS through 2032 | Weaker JSON and partial-index support than Postgres; no `CREATE INDEX CONCURRENTLY` | **Chosen** |
| PostgreSQL | Better JSON, partial and expression indexes, stronger constraint support, `CONCURRENTLY` | A full data migration on top of an already-large migration, for benefits this workload does not need at ~1k users | Rejected |
| SQLite | Trivial operations | No concurrent writer story for a multi-instance API | Rejected |

Changing engine *and* schema *and* auth *and* clients simultaneously multiplies risk for a gain that
would not be measurable at this size.

### Migration tooling

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **dbmate** (chosen) | Single static binary, runtime-agnostic; migrations are plain SQL, reviewable in a diff; up/down; checksums; trivial to run in CI and in a container entrypoint | No ORM integration; down-migrations are hand-written | **Chosen** |
| Flyway Community | Mature, widely known, similar model | JVM dependency on the deploy host | Close second |
| Prisma Migrate / Knex / TypeORM | Integrated with the app; generated migrations | Couples schema history to the Node runtime — a bad fit for a project whose backend language is *changing during the migration*; generated DDL is hard to review for a delicate expand/contract | Rejected |
| Hand-written SQL + a runbook | No tooling | This is the status quo and produced S2-02 | Rejected |

Runtime-agnosticism is the deciding property: schema history must outlive the PHP-to-TypeScript
transition, and must be applicable from CI, a container entrypoint, or a laptop with equal ease.

## Consequences

**Positive**
- The schema becomes reproducible from an empty database, which is what makes staging possible.
- Migrations are reviewable SQL in pull requests; a destructive `ALTER` is visible in the diff.
- CI can apply migrations against a **restored production backup**, not just an empty schema —
  the check that would have caught S2-02.
- `utf8mb4` makes emoji and full Unicode work in reminder text (S2-08).
- `DATETIME(3)` UTC everywhere ends the timezone defect class (S2-07).

**Negative**
- Down-migrations are hand-written and can be wrong. Mitigation: CI runs `up → rollback → up` on
  every migration PR, and destructive migrations require an explicit label and a linked rollback plan.
- The baseline is captured from production, so any drift already present is baselined in. It is
  corrected by subsequent migrations rather than pretended away — which is the honest option.
- MySQL lacks `CREATE INDEX CONCURRENTLY`, so large index builds need a low-traffic window or
  `pt-online-schema-change`. At this data size a window is sufficient.

**Neutral**
- `CHECK` over `ENUM` means adding a command type is a metadata-only `ALTER` rather than a table
  rebuild — a meaningful difference with no online-DDL tooling and one maintainer.

## Revisit when

- The dataset or write volume grows to where Postgres features (partial indexes, `CONCURRENTLY`,
  richer JSON) would materially help.
- Backup and restore operations start costing more maintainer time than a managed database would
  cost in money.
