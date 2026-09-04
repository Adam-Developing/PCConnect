# ADR-0007 — Mobile client technology

**Status:** Accepted
**Date:** 2026-09-02
**Context docs:** [06 §3](../06-client-architecture.md)

## Context

Two mobile clients exist:

- `App/` — Java Android, in production as version 7.2 (`versionCode 702`). It uses `AsyncTask`
  (deprecated since API 30), Java 8 language level against `compileSdk 35`, a mix of raw
  `HttpURLConnection` and OkHttp, `printStackTrace` as its entire error strategy (S2-13), and
  hardcoded absolute URLs across six activities. Android only.
  Its `build.gradle.kts` also declares `mysql:mysql-connector-java:8.0.27`, which nothing imports —
  no phone is opening a direct database connection, but the dependency ships in the APK.
- `mobile_flutter/` — Flutter 3, present on `main` only. Feature-first layout, Riverpod, Dio +
  Retrofit, `flutter_secure_storage`, `local_auth`, `socket_io_client`, `go_router`. Structurally
  sound, incomplete.

The Flutter client hardcodes `AppConfig.localIp = '192.168.0.113'` in a shipped build (S3-08) — a
defect to fix, not an argument against the platform.

## Decision

**Promote `mobile_flutter` to be the mobile client, targeting Android and iOS. Retire the Java
Android app after one final release** that checks `GET /v2/meta/discovery` and shows a blocking
"install the new app" prompt.

## Options considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Flutter** (chosen) | Already scaffolded correctly; iOS at no extra cost; one codebase; `flutter_secure_storage` and `local_auth` already wired for the security model; `socket_io_client` matches [ADR-0003](0003-command-channel-transport.md); good generated-client support via `openapi-generator` | A second language (Dart); larger binary than native; the maintainer is more experienced in Java | **Chosen** |
| Modernise the Java app in place | No new language; smallest step | `AsyncTask` and the whole async model need replacing anyway; still Android-only; the work approximates a rewrite without the iOS payoff | Rejected |
| Rewrite in Kotlin + Compose | Best Android experience; modern | Android only; comparable effort to Flutter for half the platform coverage | Rejected |
| React Native | Shares TypeScript with the web dashboard and the desktop frontend | Nothing exists yet; the bridge adds a failure mode for background sockets | Rejected |
| Native Android + native iOS | Best per-platform result | Two codebases for one part-time maintainer. Not viable | Rejected |

## Consequences

**Positive**
- iOS support, which the product has never had, for approximately no additional work.
- One mobile codebase.
- The security model lands naturally: `flutter_secure_storage` for the refresh token, `local_auth`
  for a biometric gate before issuing a destructive command from a phone that might be unlocked and
  in someone else's hand.
- The generated Dart client keeps mobile in lockstep with the contract; hand-written DTOs become a
  build failure.
- Deleting `App/` removes ~1,100 lines of deprecated-API Java from maintenance.

**Negative**
- Dart is a language to learn or refresh, in the middle of a migration.
- Flutter APKs are larger than the current Java app. Irrelevant for this product, but real.
- iOS release requires an Apple Developer account (annual cost) and a Mac or a CI runner for builds.
- Existing Android users must install a **new app**, not receive an update — the package id changes.
  This is a genuine user-facing cost and is why the final Java release exists: an in-app prompt
  linking straight to the new listing, plus an email to the mailing list.

**Neutral**
- Play Store listing and reviews for the old app do not carry over. At this scale, acceptable.

## Revisit when

- Flutter's background-execution constraints on iOS prevent a required feature (background socket
  wake-up is the likely candidate), which would argue for platform-native work on that path
  specifically rather than a wholesale change.
