#!/bin/sh
# Start as root (the default in docker compose): make /data writable by the app user, then drop privileges.
# Started as some other user (e.g. rootless podman with --user), just run as that user.
set -e
if [ "$(id -u)" = "0" ]; then
  chown -R shamash:shamash /data 2>/dev/null || true
  exec setpriv --reuid=shamash --regid=shamash --init-groups "$@"
fi
exec "$@"
