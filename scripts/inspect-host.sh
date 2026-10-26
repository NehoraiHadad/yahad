#!/usr/bin/env bash
set -euo pipefail
printf 'Model: '
if [[ -r /proc/device-tree/model ]]; then tr -d '\0' </proc/device-tree/model; printf '\n'; else printf 'Not identified as Raspberry Pi\n'; fi
printf 'Architecture: '; uname -m
printf 'Word size: '; getconf LONG_BIT
printf 'Memory: '; awk '/MemTotal/ {printf "%.0f MiB\n", $2/1024}' /proc/meminfo
printf 'OS:\n'; cat /etc/os-release
printf 'Disk space:\n'; df -h .
printf 'Runtime:\n'; command -v node >/dev/null && node --version || true
printf 'ffmpeg: '; command -v ffmpeg || true
printf '\nRead-only inspection only. No hardware assumptions and no installation performed.\n'
