#!/usr/bin/env bash
# =============================================================================
# Generates the secrets `deploy/.env` needs, and prints them once.
#
# It writes nothing to disk on its own: the operator pastes the output into
# `.env` (gitignored) and into their password manager. That is deliberate — a
# script that writes a KEK to a file is a script that leaves a KEK in a file.
#
# Usage:  ./deploy/env/generate-secrets.sh
# =============================================================================

set -euo pipefail

need() {
    command -v "$1" >/dev/null 2>&1 || { echo "This script needs $1." >&2; exit 1; }
}

need openssl

echo "# ── generated $(date -u +%Y-%m-%dT%H:%M:%SZ) ──────────────────────────────"
echo "# Paste into deploy/.env, then store KEK_KEY_K1 and JWT_PRIVATE_KEY_PEM in"
echo "# your password manager. Losing the KEK loses every reminder."
echo

echo "POSTGRES_PASSWORD=$(openssl rand -base64 33 | tr -d '\n')"
echo

# AES-256: exactly 32 bytes. The API refuses to start on anything else.
# The slot name is the id written to users.dek_kek_id. It starts at k1 and
# alternates with k2 on each rotation; a date-stamped id would have to match a
# slot that exists, and the one this script used to emit matched none of them,
# so the API refused to start on a freshly generated .env.
echo "KEK_CURRENT_ID=k1"
echo "KEK_KEY_K1=$(openssl rand -base64 32 | tr -d '\n')"
echo "KEK_KEY_K2="
echo

# ES256. ADR-0009 records why this is ECDSA rather than the Ed25519 ADR-0002
# specified: .NET 10 has no in-box Ed25519, and the properties that mattered —
# a pinned algorithm at the verifier, small signatures, no padding oracle — are
# the same.
key="$(openssl ecparam -name prime256v1 -genkey -noout 2>/dev/null)"
printf 'JWT_PRIVATE_KEY_PEM="%s"\n' "$(printf '%s' "$key" | sed ':a;N;$!ba;s/\n/\\n/g')"
echo

if command -v age-keygen >/dev/null 2>&1; then
    recipient="$(age-keygen 2>/dev/null | tee /dev/stderr | grep 'public key:' | cut -d' ' -f4)"
    echo "BACKUP_AGE_RECIPIENT=$recipient"
    echo
    echo "# The age PRIVATE key was printed above on stderr. Store it offline." >&2
else
    echo "# age-keygen is not installed; run it separately and set:"
    echo "BACKUP_AGE_RECIPIENT="
fi

echo
echo "# ── verify before deploying ──────────────────────────────────────────────"
echo "#  1. deploy/.env is listed in .gitignore  (git check-ignore -v deploy/.env)"
echo "#  2. gitleaks finds nothing               (gitleaks detect --no-banner)"
echo "#  3. KEK_KEY_K1 and JWT_PRIVATE_KEY_PEM are in the password manager"
