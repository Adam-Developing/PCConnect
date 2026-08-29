#!/usr/bin/env sh
set -eu

if [ "$#" -ne 1 ]; then echo "usage: $0 https://staging-api.example" >&2; exit 64; fi
test "${PCCONNECT_ENVIRONMENT:-}" = "staging" || { echo "PCCONNECT_ENVIRONMENT=staging is required" >&2; exit 1; }
base=${1%/}
case "$base" in https://*) ;; *) echo "staging URL must use HTTPS" >&2; exit 64 ;; esac
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT HUP INT TERM

live=$(curl --fail --silent --show-error --proto '=https' --tlsv1.2 "$base/api/v2/health/live")
ready=$(curl --fail --silent --show-error --proto '=https' --tlsv1.2 "$base/api/v2/health/ready")
version=$(curl --fail --silent --show-error --proto '=https' --tlsv1.2 "$base/api/v2/version")
assetlinks=$(curl --fail --silent --show-error --proto '=https' --tlsv1.2 "https://$PCCONNECT_WEBAUTHN_RP_ID/.well-known/assetlinks.json")
printf '%s' "$live" | grep -q '"status":"ok"'
printf '%s' "$ready" | grep -q '"status":"ok"'
printf '%s' "$version" | grep -q '"apiContract":"2.0.0"'
printf '%s' "$assetlinks" | grep -Fq "\"package_name\":\"$PCCONNECT_ANDROID_PACKAGE_NAME\""
printf '%s' "$assetlinks" | grep -Fq "\"$PCCONNECT_ANDROID_CERT_SHA256\""

status=$(curl --silent --output /dev/null --write-out '%{http_code}' --proto '=https' --tlsv1.2 "$base/api/v2/devices")
test "$status" = "401"

curl --silent --show-error --proto '=https' --tlsv1.2 --dump-header "$temporary/headers" --output /dev/null "$base/api/v2/version"
grep -Eiq '^strict-transport-security:' "$temporary/headers"
grep -Eiq '^x-content-type-options:[[:space:]]*nosniff' "$temporary/headers"
grep -Eiq '^x-frame-options:[[:space:]]*DENY' "$temporary/headers"
grep -Eiq "^content-security-policy:.*default-src 'none'" "$temporary/headers"
grep -Eiq '^referrer-policy:[[:space:]]*no-referrer' "$temporary/headers"

http_base="http://${base#https://}"
redirect=$(curl --silent --output /dev/null --write-out '%{http_code}' --proto '=http' "$http_base/api/v2/version")
test "$redirect" = "301" || test "$redirect" = "308"

curl --silent --show-error --proto '=https' --tlsv1.2 --request OPTIONS \
  --header 'Origin: https://hostile.example.invalid' \
  --header 'Access-Control-Request-Method: GET' \
  --dump-header "$temporary/cors" --output /dev/null "$base/api/v2/devices"
if grep -Eiq '^access-control-allow-origin:' "$temporary/cors"; then
  echo "unapproved CORS origin was reflected" >&2
  exit 1
fi

printf '{"login":"' > "$temporary/oversized.json"
dd if=/dev/zero bs=1048577 count=1 2>/dev/null | tr '\000' x >> "$temporary/oversized.json"
printf '","password":"x","client":{"platform":"android","name":"smoke","version":"1"}}' >> "$temporary/oversized.json"
too_large=$(curl --silent --output "$temporary/too-large-response" --write-out '%{http_code}' --proto '=https' --tlsv1.2 \
  --header 'Content-Type: application/json' --data-binary "@$temporary/oversized.json" "$base/api/v2/auth/password/login")
test "$too_large" = "413"
if grep -Eiq 'System\.|stack trace|Npgsql| at [A-Za-z0-9_.]+\(' "$temporary/too-large-response"; then
  echo "exception implementation detail leaked from staging" >&2
  exit 1
fi
echo "staging smoke verification passed: $version"
