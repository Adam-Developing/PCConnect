# PCConnect v2 database artifacts

- `v2-canonical-schema.sql` is the approved PostgreSQL 18 target model.
- `legacy-mapping.md` is the migration worksheet and mapping policy.
- `pcconnect.sql` is a stale, sensitive legacy dump and is neither a migration
  source nor a schema definition.

Implementation must translate `v2-canonical-schema.sql` into ordered EF Core
migrations and prove that a clean PostgreSQL 18 database reaches an equivalent
shape. Do not run this architecture file directly against production.

Production migration uses a read-only current export, stable `legacy_id_map`
records, dry runs and signed reconciliation manifests. Development and tests use
deterministic synthetic fixtures only.
