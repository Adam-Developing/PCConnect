# Retired PHP source

This directory is retained only as migration evidence. Every PHP entry point
returns `410 Gone`; it must not be deployed. The .NET v2 API owns the historical
route shapes during the bounded strangler window and applies keyed credentials,
route scopes, authorization, audit, idempotency, and the day-45/day-60 sunset.

Do not re-enable the code under `src`. Production routing must point only to the
digest-pinned v2 Compose deployment.
