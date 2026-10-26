# Root service mode-control repair

Code inspection and a real disposable systemd mount-sandbox test identified a configuration defect: `yachad-update.service` has `ProtectSystem=strict`, but its writable paths exclude the parent directory needed for atomic replacement of `/etc/yachad/updates.json`. A mode request may remain pending with configuration errors. A pending root intent is processed before normal release checks, so it can also delay discovery/install of a new Release.

This is an **owner-operated OS configuration repair**, not an application update. Publishing or installing 0.3.3 does not apply it. No device state or service was changed during development.

## Narrow proposal for the known existing installation

Before applying, inspect only the effective root unit paths and sanitized update status, and confirm whether its pending request matches the parent's intended mode. Do not post environment, private key, backup secret or raw logs. The next timer pass may apply a previously pending mode request once permissions are repaired.

Create `/etc/systemd/system/yachad-update.service.d/40-mode-control.conf` as root, mode 0644, without overwriting any existing different file:

```ini
[Service]
ReadWritePaths=/etc/yachad
ReadOnlyPaths=/etc/yachad/yachad.env /etc/yachad/whatsapp.key /etc/yachad/update-backup-passphrase
```

Then run `sudo systemctl daemon-reload`. This does not restart the app or change the active mode directly. Keep the existing `ProtectSystem=strict` and other writable paths. The existing periodic worker will process requests under the corrected sandbox on its next run.

Only `/etc/yachad` is added, not all of `/etc`. A writable parent directory is necessary for `updates.json.tmp` plus atomic rename. The live key, application environment and backup secret remain read-only mount points, including against replacement/rename. For other installations, use their actual key path; if signature trust or a GitHub token is configured, add those root files to `ReadOnlyPaths` too. Do not change repository/trust/operator fields or invent a new mode request as part of this repair.

Afterward verify the UI's applied mode and checked-at/version fields. A queued request or a successful unit start is not proof that a mode change or application update completed. If the effective unit differs or the override already exists with different content, preserve it for local review.

## Development evidence and limits

The baseline transient systemd fixture rejected atomic writes. With the narrow override, config creation/rename succeeded while synthetic key/env/backup-secret writes **and renames** remained blocked. The fixture used temporary paths and `PrivateNetwork=yes`; it did not touch Yachad services, production files or real accounts. The owner's actual unit and pending state still require owner inspection and acceptance.
