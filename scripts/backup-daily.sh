#!/usr/bin/env bash
# For an operator on the actual Pi after installation/approval. Never run as part of a web request.
# Prompts locally for an independent backup passphrase. No secrets in process arguments or shell history.
set -euo pipefail
if [[ $(id -u) -ne 0 ]]; then printf 'Run locally with sudo after reviewing deployment.\n' >&2; exit 1; fi
systemctl stop yachad
trap 'systemctl start yachad' EXIT
cd /opt/yachad/current
set -a
source /etc/yachad/yachad.env
set +a
install -d -m 0700 /var/backups/yachad
node --import tsx scripts/backup.ts create /var/backups/yachad
# Retention also expires old archives if used separately on days with no new backup.
find /var/backups/yachad -maxdepth 1 -type f -name 'yachad-*.backup' -mtime +6 -delete
