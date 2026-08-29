# PCConnect contracts

These files are public compatibility boundaries, not illustrative pseudocode.

| Artifact | Version | Consumers |
|---|---:|---|
| `openapi-v2.json` | 2.0.0 | API, worker callbacks, Android, Windows and future clients |
| `realtime-v2.json` | 2.0.0 | SignalR hubs and reconnect/catch-up clients |
| `named-pipe-v1.schema.json` | 1 | Windows service and WPF companion |
| `migration-manifest.schema.json` | 1 | Importer, reconciliation gate and audit archive |

## Versioning

- Additive optional fields and endpoints may ship in the current major version.
  Consumers ignore unknown response/event fields.
- Removing/renaming fields, tightening accepted values, changing enum meaning or
  changing state transitions requires a new major contract.
- New enum values are breaking until all generated clients explicitly implement
  an `unknown` handling path; do not add them casually.
- REST deprecation uses `Deprecation`, `Sunset` and `Link` headers and remains
  supported for the published window. The migration compatibility API is the
  sole exception: its sunset is fixed at 60 days and cannot be extended by an
  ordinary configuration change.
- SignalR events are hints. Adding an event does not replace a REST resource or
  remove cursor recovery.

## Generation policy

- Generate C# DTOs and Kotlin models from `openapi-v2.json` in CI and compile
  them against their consumers.
- Keep domain entities separate from transport DTOs. Generated files are never
  manually edited.
- SignalR and named-pipe models are generated from their schemas or wrapped by a
  small, tested adapter using the exact schema names/enums.
- Example files are synthetic and contain no production identifiers or data.

## Validation

Run:

```text
python contracts/check_contracts.py
```

The dependency-free check validates JSON, local references, required security,
examples, shared enums and the canonical SQL vocabulary. CI must also use
standards-compliant OpenAPI 3.1, AsyncAPI 3.0 and JSON Schema 2020-12 validators
before generating clients.
