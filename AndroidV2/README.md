# PCConnect Android v2

This is the Kotlin/Jetpack Compose replacement client. It targets Android API
36, supports API 24 and later, uses Credential Manager for passkeys, wraps
refresh material with Android Keystore, excludes credentials and cached data
from backup/transfer, and combines SignalR hints with cursor-based REST recovery.

Use the checksum-pinned wrapper from this directory:

```sh
./gradlew -p . testDebugUnitTest lintDebug assembleDebug --no-daemon
```

Release builds are deliberately fail-closed. Protected CI must provide
`PCCONNECT_ANDROID_KEYSTORE`, `PCCONNECT_ANDROID_STORE_PASSWORD`,
`PCCONNECT_ANDROID_KEY_ALIAS`, and `PCCONNECT_ANDROID_KEY_PASSWORD`. The release
workflow runs strict lint and R8 shrinking, creates a signed AAB, and verifies
its JAR signature. The keystore and passwords never belong in source or build
artifacts.

The AAB is an upload artifact. Enrol the application in Google Play App Signing
and keep the upload key distinct from the Play-managed app-signing key. Upload
to an internal staging track and complete device/passkey/recovery acceptance
before requesting any production rollout.

Because the v2 application keeps the existing package name, an in-place update
also requires the existing app-signing identity or an approved Play signing-key
upgrade/lineage. Do not substitute a new local key: that would strand installed
clients and contradict the data-preserving migration plan.
