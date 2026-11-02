# Full installer timing and partial-install recovery

The ASUS test used the exact 2edd777 installer, Notify and loopback-only. Its
elevated child started at 17:32:03 (Asia/Jerusalem); the four fixed services
were created at 17:36:44. The helper recorded FullProvision/INSTALL_TIMEOUT
at 17:44:04. All services remained stopped1077/PID0. Process counters were
still advancing before cancellation. CPU saturation was observed separately;
that snapshot does not establish the cause of the timeout. No retry, manual
cleanup or security change was performed after this failed attempt.

The original single12minute deadline covered validation, extraction, verified
publication, cleanup and startup. Historical counters and the private source
manifest model indicate roughly2200–3300of9337payloadmembers at the last sample,
about6minutes into publication. This is a model, not protected on-disk proof.
A separate12minute publication allocation can therefore still be too short.
The simpler fix gives full-profile installation one non-refreshing48minute
absolute limit, within the earlier nominal48minute maximum. Full-profile removal
has a separate absolute96minute limit; legacy installation/removal and receipt
repair remain12minutes. Both peers derive the profile from their
own embedded manifest; request fields cannot select it. No progress event renews
the deadline. Publication and cleanup report bounded progress.

The subsequent exact E045 removal helper started at19:59:06 Asia/Jerusalem.
Its read counters advanced from924914224bytes at19:59:16 to1754408452bytes at
20:11:02; it had not published a removal intent. It recorded
FullUninstall/INSTALL_TIMEOUT at20:11:53 and exited. All four services remained
stopped1077/PID0 and both roots remained. No deletion was observed. The old
12minute limit expired during the repeated independent inventory/ownership/hash
proofs. The96minute full-removal ceiling allows those proofs and the existing
receipt-bound deletion checks; it is a bounded allowance, not a completion-time
guarantee. Those proofs are not skipped or cached across mutation boundaries.
Inventory traversal and each hash chunk now propagate cancellation, so deadline
expiry does not leave an uncancellable inventory scan. Existing handles still
dispose normally. A multi-chunk synthetic test cancels after the first read,
checks that no second read occurs, and retains the exact size/hash refusal.

A same-package retry reuses one canonical protected staging directory. Its
closed inventory is checked before resuming extraction. Every existing byte
must match the exact source entry, whose entire size/hash is verified before
a truncated staging file can be handle-bound deleted/recreated. Complete
matching files remain. Unknown/changed/linked members refuse. Interrupted
committed cleanup uses the same-package manifest and existing maintenance
quiescence guard; other-package stages are not adopted.

A separate source mismatch was found between A/B boot semantics and installer
startup: a healthy no-operation boot exits STOPPED0 without staying RUNNING.
A/B now require a new successful StartService request and bounded clean
completion, exact fixed service identity/protection, and zero process exit if
a generation was observed. Runtime/Authority start behavior is unchanged.
Process-generation capture is restricted to RUNNING: pending-state PID values
are not documented as valid. See Microsoft's
[SERVICE_STATUS_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status_process)
and [StartServiceW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicew).

Existing Format5 cancellation now also covers a narrow payload-only preparation
after role assignment: four fixed protected demand services never started;
no enrollment/plan/policy or user-data files; empty binary root; exact protected
stage and source-manifest payload copies only. Original Owner, durable intent,
manifest hashes and receipt-last deletion remain. Unknown copy partials, native
binary publication, enrollment and data refuse. Runtime is removed last.
There is no legacy migration, manual receipt conversion or digest adoption.

Initial focused regressions passed99cases plus framing and two synthetic UI
cases. Independent security/minimality review found two one-shot checks, both
fixed: cancellation immediately before launch and observed zero exit status.
Final frozen-source build/regressions and native acceptance are recorded
separately in the delivery evidence. These source checks are not a successful
Windows installation or permission to apply an update.
