#!/usr/bin/env bash
# One-time setup of Palanca on the VPS, safe to run again: the project directory, the backup directory, and the
# daily backup timer. Run it as root from a checkout of the repository:
#
#   sudo deploy/vps/install.sh
#
# Then deploy an image with /opt/palanca/deploy.sh.
set -euo pipefail

source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_dir=/opt/palanca
# The non-root user of the .NET runtime images ($APP_UID), which writes the backups.
app_uid=1654

if [[ $EUID -ne 0 ]]; then
  echo "Run this as root." >&2
  exit 1
fi

install -d -m 0755 -o root -g root "$project_dir"
install -d -m 0700 -o "$app_uid" -g "$app_uid" "$project_dir/backups"
install -m 0644 -o root -g root "$source_dir/compose.yml" "$project_dir/compose.yml"
install -m 0755 -o root -g root "$source_dir/vps/deploy.sh" "$project_dir/deploy.sh"
install -m 0644 -o root -g root "$source_dir/vps/palanca-backup.service" /etc/systemd/system/palanca-backup.service
install -m 0644 -o root -g root "$source_dir/vps/palanca-backup.timer" /etc/systemd/system/palanca-backup.timer

systemctl daemon-reload
systemctl enable --now palanca-backup.timer
echo "Installed into $project_dir. Deploy with: $project_dir/deploy.sh ghcr.io/yannroux/palanca@sha256:<digest>"
