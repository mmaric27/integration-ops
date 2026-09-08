#!/usr/bin/env bash
set -euo pipefail

for integration_ops_password in \
    "${POSTGRES_PASSWORD-}" \
    "${INTEGRATION_OPS_DB_PASSWORD-}"; do
    case "$integration_ops_password" in
        ""|REPLACE_ME*)
            printf '%s\n' \
                "Database initialization requires private non-placeholder passwords." >&2
            exit 1
            ;;
    esac
done
unset integration_ops_password

psql --no-psqlrc \
    --set=ON_ERROR_STOP=1 \
    --username "$POSTGRES_USER" \
    --dbname postgres <<'SQL'
\getenv app_password INTEGRATION_OPS_DB_PASSWORD

CREATE ROLE integration_ops
    LOGIN
    NOSUPERUSER
    NOCREATEDB
    NOCREATEROLE
    NOREPLICATION
    PASSWORD :'app_password';

\unset app_password

CREATE DATABASE integration_ops_dev OWNER integration_ops;
SQL
