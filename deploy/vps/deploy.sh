#!/usr/bin/env bash
# Deploys one Palanca image, by digest only (FLG-071), and waits until it is healthy. Run it as root:
#
#   sudo /opt/palanca/deploy.sh ghcr.io/yannroux/palanca@sha256:<digest>
#
# To roll back, run it again with the previous digest, which it prints. A newer image may have migrated the
# database; the copy it took before migrating is in /opt/palanca/backups.
#
# PALANCA_DIR replaces /opt/palanca, to rehearse the deployment elsewhere.
set -euo pipefail

image="${1:-}"
if [[ ! "$image" =~ ^ghcr\.io/yannroux/palanca@sha256:[0-9a-f]{64}$ ]]; then
  echo "Usage: $0 ghcr.io/yannroux/palanca@sha256:<64 hex digits>" >&2
  exit 2
fi

project_dir="${PALANCA_DIR:-/opt/palanca}"
cd "$project_dir"

for network in hogarhero-production_edge hogarhero-production_backend hogarhero-production_staging-backend; do
  if ! docker network inspect "$network" > /dev/null 2>&1; then
    echo "The Docker network $network does not exist. Start HogarHero's Compose project first." >&2
    exit 1
  fi
done

if [[ -f .env ]]; then
  echo "Previous image: $(sed -n 's/^PALANCA_IMAGE=//p' .env)"
fi

umask 077
printf 'PALANCA_IMAGE=%s\n' "$image" > .env.next
mv .env.next .env

docker compose pull --quiet
docker compose up --detach --wait --wait-timeout 120
docker compose ps
