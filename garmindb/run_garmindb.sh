#!/usr/bin/env bash
set -euo pipefail

CONFIG_DIR="${HOME}/.GarminDb"
mkdir -p "$CONFIG_DIR"

CONFIG_FILE="$CONFIG_DIR/GarminConnectConfig.json"

cat > "$CONFIG_FILE" <<EOF
{
  "Username": "${GARMIN_USERNAME:-}",
  "Password": "${GARMIN_PASSWORD:-}",
  "UseOAUTH": false,
  "BaseFolder": "/data",
  "DatabaseLocation": "${GARMIN_OUTPUT_DB:-/data/garmin.sqlite}"
}
EOF

SLEEP_MINUTES=${GARMIN_REFRESH_MINUTES:-60}

while true; do
  echo "Starting GarminDB run at $(date --iso-8601=seconds)"
  python garmindb_cli.py ${GARMIN_FLAGS} --db ${GARMIN_OUTPUT_DB} || echo "garmindb run failed"
  echo "Sleeping ${SLEEP_MINUTES} minutes..."
  sleep $((SLEEP_MINUTES * 60))
done

