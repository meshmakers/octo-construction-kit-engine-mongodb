#!/usr/bin/env bash
# AB#7727: tells whether a tenant needs a rollup backfill for manual_count / last_manual_at.
#
# Every rollup offers the two columns on its own after the platform update, and empty reads as 0. So a tenant
# whose raw and time-range archives hold no manual row needs nothing. A tenant with manual rows gets the
# values of its older buckets from a normal backfill of the rollups over those archives, over the printed
# range (recomputeArchive; a rollup over a rollup follows).
#
# Read only. It counts on row_origin, an indexed column, per archive table of one tenant schema.
#
#   rollup-manual-rows-check.sh <cratedb-http-url> <tenant-schema>
#
# <tenant-schema> is the CrateDB schema of the tenant, the output of TenantSchema.SchemaName.
# Credentials, if the cluster wants them: CRATE_USER and CRATE_PASSWORD. Requires curl and python3.
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <cratedb-http-url> <tenant-schema>" >&2
  exit 2
fi

url="${1%/}/_sql"
schema="$2"

sql() {
  local body
  body="$(python3 -c 'import json,sys; print(json.dumps({"stmt": sys.argv[1]}))' "$1")"
  if [ -n "${CRATE_USER:-}" ]; then
    # Through a curl config on stdin: on the command line ps would show the password.
    local credentials="$CRATE_USER:${CRATE_PASSWORD:-}"
    credentials="${credentials//\\/\\\\}"
    credentials="${credentials//\"/\\\"}"
    printf 'user = "%s"\n' "$credentials" \
      | curl -K - -sS --fail-with-body -H 'Content-Type: application/json' -d "$body" "$url"
  else
    curl -sS --fail-with-body -H 'Content-Type: application/json' -d "$body" "$url"
  fi
}

rows() { python3 -c 'import json,sys; [print("\t".join("" if v is None else str(v) for v in r)) for r in json.load(sys.stdin)["rows"]]'; }

tables="$(sql "SELECT table_name FROM information_schema.columns WHERE table_schema = '$schema' AND column_name = 'row_origin' ORDER BY 1" | rows)"

total=0
while IFS= read -r table; do
  [ -n "$table" ] || continue
  line="$(sql "SELECT COUNT(*), MIN(manual_written_at), MAX(manual_written_at) FROM \"$schema\".\"$table\" WHERE row_origin = 'manual'" | rows)"
  count="${line%%$'\t'*}"
  if [ "$count" -gt 0 ]; then
    echo "$table: $count manual rows"
    time_column="$(sql "SELECT column_name FROM information_schema.columns WHERE table_schema = '$schema' AND table_name = '$table' AND column_name = 'window_start'" | rows)"
    time_column="${time_column:-timestamp}"
    sql "SELECT MIN(\"$time_column\"), MAX(\"$time_column\") FROM \"$schema\".\"$table\" WHERE row_origin = 'manual'" \
      | python3 -c 'import json,sys,datetime as d; lo,hi=json.load(sys.stdin)["rows"][0]; f=lambda ms: d.datetime.fromtimestamp(ms/1000,d.timezone.utc).isoformat(); print("  covers", f(lo), "to", f(hi))'
    total=$((total + count))
  fi
done <<< "$tables"

if [ "$total" -eq 0 ]; then
  echo "NO BACKFILL NEEDED: no manual rows in schema $schema."
else
  echo "BACKFILL NEEDED: $total manual rows in schema $schema; recompute the rollups over the archives listed above, over the ranges shown."
fi
