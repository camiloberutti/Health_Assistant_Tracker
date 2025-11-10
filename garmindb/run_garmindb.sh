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

  # After a normal run, check for an immediate import trigger file placed in the data volume.
  # If present, perform an import immediately and then remove the trigger file.
  TRIGGER_FILE="${GARMIN_OUTPUT_DB%/*}/import_now"
  if [ -f "$TRIGGER_FILE" ]; then
    echo "Import trigger detected at $TRIGGER_FILE — running additional import"
    python garmindb_cli.py ${GARMIN_FLAGS} --db ${GARMIN_OUTPUT_DB} || echo "garmindb trigger run failed"
    rm -f "$TRIGGER_FILE" || true
  fi

  echo "Sleeping ${SLEEP_MINUTES} minutes..."
  sleep $((SLEEP_MINUTES * 60))
done

