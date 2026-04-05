#!/bin/bash
set -euo pipefail

# Ensures the host AIO limit is sufficient for Scylla/Seastar startup.
# Required minimum is 219641; recommended is 269640.
MIN_REQUIRED="${AIO_MIN_REQUIRED:-219641}"
TARGET="${AIO_TARGET:-269640}"
AIO_SYSCTL_PATH="/proc/sys/fs/aio-max-nr"

if [ ! -r "$AIO_SYSCTL_PATH" ]; then
  echo "ERROR: Cannot read $AIO_SYSCTL_PATH"
  exit 1
fi

current="$(cat "$AIO_SYSCTL_PATH")"
echo "Current fs.aio-max-nr: $current"

if [ "$current" -ge "$MIN_REQUIRED" ]; then
  echo "Host AIO limit already satisfies minimum requirement ($MIN_REQUIRED)."
  exit 0
fi

echo "Host AIO limit is below minimum ($MIN_REQUIRED). Attempting to set to recommended value ($TARGET)..."
if echo "$TARGET" > "$AIO_SYSCTL_PATH"; then
  updated="$(cat "$AIO_SYSCTL_PATH")"
  echo "Updated fs.aio-max-nr: $updated"
else
  echo "ERROR: Failed to update fs.aio-max-nr."
  echo "Set it manually on the host and retry:"
  echo "  sudo sysctl -w fs.aio-max-nr=$TARGET"
  exit 1
fi

if [ "$updated" -lt "$MIN_REQUIRED" ]; then
  echo "ERROR: fs.aio-max-nr is still below minimum requirement after update."
  exit 1
fi

echo "Host AIO tuning complete."
