# System architecture

## Context

```mermaid
flowchart LR
    Owner[PCConnect account owner]
    Android[Android controller\nKotlin + Compose]
    Future[Future iOS controller]
    Win[Windows device\nService + companion]
    Unix[Future Linux/macOS agent]
    Mail[Transactional email provider]
    PC2[PCConnect v2]

    Owner --> Android
    Owner -. future .-> Future
    Android -->|HTTPS REST + SignalR| PC2
    Future -. same contracts .-> PC2
    Win -->|outbound HTTPS REST + SignalR| PC2
    Unix -. same agent contracts .-> PC2
    PC2 -->|verification, reset, alerts| Mail
```

PCConnect never accepts inbound connections to a managed PC. Every device opens
an authenticated outbound connection to the public service.

## Containers

```mermaid
flowchart TB
    subgraph Clients
      A[Android controller]
      S[Windows Service]
      U[WPF companion]
      S <-->|ACL-restricted named pipe| U
    end

    subgraph VPS[Single production VPS]
      C[Caddy edge/TLS]
      API[ASP.NET Core API\nREST + SignalR]
      W[.NET Worker]
      PG[(PostgreSQL 18\nsource of truth)]
      V[(Valkey\ncache/rate limits/backplane)]
      O[OpenTelemetry collector]
      C --> API
      API --> PG
      W --> PG
      API --> V
      W --> V
      API --> O
      W --> O
    end

    A -->|HTTPS| C
    S -->|outbound HTTPS| C
    API -->|transactional outbox| PG
    W -->|publish hints| V
```

The API and worker are separate processes built from one .NET solution. The API
owns synchronous validation and transactions; the worker owns schedules,
outbox dispatch, expiry, export/deletion and retention jobs. Neither keeps
authoritative in-memory domain state.

## Backend modules

```mermaid
flowchart LR
    Edge[HTTP/SignalR edge]
    Identity[Identity]
    Accounts[Accounts]
    Devices[Devices]
    Commands[Commands]
    Reminders[Reminders]
    Audit[Audit]
    Compat[Migration compatibility]
    Ops[Operations]
    Outbox[(Transactional outbox)]

    Edge --> Identity
    Edge --> Accounts
    Edge --> Devices
    Edge --> Commands
    Edge --> Reminders
    Compat --> Identity
    Compat --> Devices
    Compat --> Commands
    Compat --> Reminders
    Identity --> Audit
    Accounts --> Audit
    Devices --> Audit
    Commands --> Audit
    Reminders --> Audit
    Devices --> Outbox
    Commands --> Outbox
    Reminders --> Outbox
    Ops --> Outbox
```

Modules have separate namespaces, application interfaces and table ownership.
Cross-module changes use application services inside the request transaction or
typed outbox messages after commit. Direct cross-module table writes are
forbidden. The first deployment is a modular monolith, not microservices.

## Windows runtime

```mermaid
flowchart TB
    Cloud[PCConnect v2]
    Service[Windows Service\nnetwork + machine identity]
    Store[DPAPI LocalMachine\nservice-only credential]
    Broker[Typed command broker]
    Companion[Per-user WPF companion]
    Session[Interactive Windows session]
    Machine[Machine power APIs]

    Cloud <-->|REST + SignalR| Service
    Service <--> Store
    Service --> Broker
    Broker -->|shutdown/restart/sleep/hibernate| Machine
    Broker <-->|named pipe v1| Companion
    Companion -->|lock/sign-out/reminders| Session
```

- The service starts before sign-in, authenticates the enrolled device and owns
  command claims/acknowledgements.
- The service runs under a dedicated service identity with only the rights
  proven necessary. LocalSystem is allowed only if integration testing proves a
  virtual service account cannot perform required machine-level operations.
- The companion validates the server-issued operation and active user SID. It
  cannot ask the service to run arbitrary programs.
- Lock and sign-out fail with `no_interactive_session` when no approved session
  exists. Reminder deliveries remain pending until a companion connects.

## Android runtime

The Android controller uses Kotlin, Compose, coroutines/Flow, ViewModels,
Retrofit/OkHttp, Room, DataStore and WorkManager. It targets API 36 with minimum
API 24. Credential Manager passkeys are enabled on Android 9+; password login is
retained on API 24–27. Refresh credentials are wrapped by Android Keystore and
excluded from cloud backup and device transfer.

Room stores read models and cursors only. Commands, enrollment approvals,
credential changes, exports and deletion are online-only and are never placed
in an offline write queue.

## Extensibility contract

`platform` is descriptive rather than an authorization shortcut. Every agent
registers protocol version and capabilities. Server authorization checks the
user, device state, advertised capability, command risk and step-up grant.
Adding Linux/macOS therefore requires a new executor and secure local credential
store, not a backend route or schema change. An iOS controller consumes the same
controller contract and advertises no agent capabilities.
