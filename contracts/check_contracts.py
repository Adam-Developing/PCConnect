#!/usr/bin/env python3
"""Validate PCConnect architecture contracts using only the Python stdlib.

This is deliberately dependency-free so CI can run it before installing client
or server toolchains. A full implementation must additionally run an OpenAPI
3.1 validator, an AsyncAPI validator and JSON Schema example validation.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parents[1]
CONTRACTS = ROOT / "contracts"
SQL_PATH = ROOT / "DB" / "v2-canonical-schema.sql"


def fail(message: str) -> None:
    raise AssertionError(message)


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"{path.relative_to(ROOT)} is not valid JSON: {exc}")


def resolve_pointer(document: Any, pointer: str) -> Any:
    if not pointer.startswith("#/"):
        fail(f"Only local JSON pointers are permitted, found {pointer!r}")
    current = document
    for raw in pointer[2:].split("/"):
        token = raw.replace("~1", "/").replace("~0", "~")
        if isinstance(current, dict) and token in current:
            current = current[token]
        else:
            fail(f"Unresolved JSON pointer {pointer!r}")
    return current


def walk_refs(value: Any) -> Iterable[str]:
    if isinstance(value, dict):
        ref = value.get("$ref")
        if isinstance(ref, str):
            yield ref
        for nested in value.values():
            yield from walk_refs(nested)
    elif isinstance(value, list):
        for nested in value:
            yield from walk_refs(nested)


def sql_enum(sql: str, enum_name: str) -> list[str]:
    pattern = rf"CREATE TYPE\s+{re.escape(enum_name)}\s+AS ENUM\s*\((.*?)\);"
    match = re.search(pattern, sql, flags=re.IGNORECASE | re.DOTALL)
    if not match:
        fail(f"SQL enum {enum_name!r} not found")
    return re.findall(r"'([^']+)'", match.group(1))


def expect_equal(label: str, *values: list[str]) -> None:
    first = values[0]
    for value in values[1:]:
        if value != first:
            fail(f"{label} mismatch: {values!r}")


def validate_openapi(openapi: dict[str, Any]) -> None:
    if openapi.get("openapi") != "3.1.0":
        fail("OpenAPI contract must use 3.1.0")
    if openapi.get("info", {}).get("version") != "2.0.0":
        fail("OpenAPI public version must be 2.0.0")
    servers = openapi.get("servers", [])
    expected = "https://api.pcconnect.adamdeveloping.co.uk/api/v2"
    if not any(item.get("url") == expected for item in servers):
        fail(f"OpenAPI production server must be {expected}")

    operation_ids: set[str] = set()
    for route, path_item in openapi.get("paths", {}).items():
        for method, operation in path_item.items():
            if method not in {"get", "post", "put", "patch", "delete", "options", "head"}:
                continue
            operation_id = operation.get("operationId")
            if not operation_id:
                fail(f"{method.upper()} {route} has no operationId")
            if operation_id in operation_ids:
                fail(f"Duplicate operationId {operation_id!r}")
            operation_ids.add(operation_id)
            responses = operation.get("responses", {})
            if not responses:
                fail(f"{method.upper()} {route} has no responses")

    public_paths = {
        "/health/live", "/health/ready", "/version", "/auth/register",
        "/auth/password/login", "/auth/refresh", "/auth/password/forgot",
        "/auth/password/reset", "/auth/email/verify",
        "/auth/passkeys/authentication/options",
        "/auth/passkeys/authentication/complete",
        "/device-enrollments", "/device-enrollments/token", "/agent/auth/refresh",
    }
    for route, path_item in openapi["paths"].items():
        for method, operation in path_item.items():
            if method not in {"get", "post", "put", "patch", "delete"}:
                continue
            security = operation.get("security", openapi.get("security"))
            if route in public_paths and security != []:
                fail(f"Public route {method.upper()} {route} must explicitly set security: []")
            if route not in public_paths and security == []:
                fail(f"Protected route {method.upper()} {route} unexpectedly disables security")

    command_post = openapi["paths"]["/devices/{deviceId}/commands"]["post"]
    parameter_refs = {p.get("$ref") for p in command_post.get("parameters", [])}
    if "#/components/parameters/IdempotencyKey" not in parameter_refs:
        fail("Command creation must require Idempotency-Key")

    reminder_post = openapi["paths"]["/reminders"]["post"]
    reminder_parameter_refs = {p.get("$ref") for p in reminder_post.get("parameters", [])}
    if "#/components/parameters/IdempotencyKey" not in reminder_parameter_refs:
        fail("Reminder creation must require Idempotency-Key")

    problem = openapi["components"]["schemas"]["Problem"]
    for member in ("type", "title", "status", "code", "correlationId"):
        if member not in problem.get("required", []):
            fail(f"Problem details must require {member}")


def validate_realtime(realtime: dict[str, Any]) -> None:
    if realtime.get("asyncapi") != "3.0.0":
        fail("Realtime contract must use AsyncAPI 3.0.0")
    if realtime.get("info", {}).get("version") != "2.0.0":
        fail("Realtime public version must be 2.0.0")
    messages = realtime["components"]["messages"]
    required = {
        "CommandAvailable", "CommandStatusChanged", "DevicePresenceChanged",
        "ReminderChanged", "SessionRevoked",
    }
    if set(messages) != required:
        fail(f"Realtime event set changed: {sorted(messages)}")
    for name, message in messages.items():
        if message.get("name") != name:
            fail(f"Realtime message key/name mismatch for {name}")


def validate_examples(examples: dict[str, Any], openapi: dict[str, Any], realtime: dict[str, Any]) -> None:
    command = examples["command-lifecycle.json"]
    command_types = openapi["components"]["schemas"]["CommandType"]["enum"]
    command_states = openapi["components"]["schemas"]["CommandStatus"]["enum"]
    if command["create"]["type"] not in command_types:
        fail("Command example contains an unknown command type")
    if command["queued"]["status"] not in command_states:
        fail("Command example contains an unknown state")
    if command["queued"]["id"] != command["acknowledgement"]["localReplayKey"]:
        fail("Command example localReplayKey must equal command ID")

    reminder = examples["reminder.json"]
    target_modes = openapi["components"]["schemas"]["ReminderTargetMode"]["enum"]
    if reminder["write"]["targetMode"] not in target_modes:
        fail("Reminder example contains an unknown target mode")
    if reminder["write"]["targetMode"] == "selected_devices" and not reminder["write"].get("targetDeviceIds"):
        fail("Selected-device reminder example requires targetDeviceIds")

    known_events = set(realtime["components"]["messages"])
    for event in examples["realtime-events.json"]:
        if event.get("eventType") not in known_events:
            fail(f"Realtime example contains unknown event {event.get('eventType')!r}")
        for required in ("eventId", "entityId", "entityVersion", "occurredAt", "payload"):
            if required not in event:
                fail(f"Realtime example omits {required}")


def main() -> int:
    json_paths = sorted(CONTRACTS.glob("*.json")) + sorted((CONTRACTS / "examples").glob("*.json"))
    documents = {path.name: load_json(path) for path in json_paths}
    openapi = documents["openapi-v2.json"]
    realtime = documents["realtime-v2.json"]
    pipe = documents["named-pipe-v1.schema.json"]
    sql = SQL_PATH.read_text(encoding="utf-8")

    for path in json_paths:
        document = documents[path.name]
        for ref in walk_refs(document):
            resolve_pointer(document, ref)

    validate_openapi(openapi)
    validate_realtime(realtime)
    validate_examples(documents, openapi, realtime)

    oa = openapi["components"]["schemas"]
    rt = realtime["components"]["schemas"]
    pipe_defs = pipe["$defs"]
    expect_equal(
        "command type",
        oa["CommandType"]["enum"],
        pipe_defs["commandType"]["enum"],
        sql_enum(sql, "command_type"),
    )
    expect_equal(
        "command status",
        oa["CommandStatus"]["enum"],
        rt["CommandStatus"]["enum"],
        sql_enum(sql, "command_status"),
    )
    expect_equal(
        "command failure code",
        oa["CommandFailureCode"]["enum"],
        [value for value in pipe_defs["executeResult"]["allOf"][1]["properties"]["failureCode"]["enum"] if value is not None],
        sql_enum(sql, "command_failure_code"),
    )
    expect_equal(
        "platform",
        oa["Platform"]["enum"],
        sql_enum(sql, "platform_type"),
    )
    expect_equal(
        "device capability",
        oa["DeviceCapability"]["enum"],
        sql_enum(sql, "device_capability"),
    )
    expect_equal(
        "reminder target mode",
        oa["ReminderTargetMode"]["enum"],
        sql_enum(sql, "reminder_target_mode"),
    )

    required_tables = {
        "users", "password_credentials", "passkeys", "sessions",
        "session_refresh_tokens", "access_tokens", "email_outbox", "devices",
        "device_credentials", "device_enrollments", "commands",
        "command_events", "reminders", "reminder_targets",
        "reminder_occurrences", "reminder_deliveries", "outbox_messages",
        "audit_events", "legacy_id_map", "legacy_compat_credentials",
        "data_export_jobs", "account_deletion_jobs", "deletion_tombstones",
    }
    sql_tables = set(re.findall(r"CREATE TABLE\s+([a-z_]+)\s*\(", sql, flags=re.IGNORECASE))
    missing_tables = required_tables - sql_tables
    if missing_tables:
        fail(f"Canonical SQL omits required tables: {sorted(missing_tables)}")
    if re.search(r"CREATE TABLE\s+reminders.*?\btext\s+(?:text|varchar)", sql, flags=re.IGNORECASE | re.DOTALL):
        fail("Canonical reminders table must not contain a plaintext text column")
    for encrypted_column in ("text_ciphertext", "text_nonce", "text_tag", "wrapped_data_key", "wrapping_key_id"):
        if encrypted_column not in sql:
            fail(f"Canonical reminder encryption omits {encrypted_column}")

    print(f"Validated {len(json_paths)} JSON artifacts, local references, examples, shared enums, security rules, and canonical SQL vocabulary.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AssertionError as exc:
        print(f"contract validation failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
