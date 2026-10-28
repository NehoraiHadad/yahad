# Exact historical updater fixture

`updater-legacy-0.4.txt` is the unmodified canonical source at
`07be02f:deploy/updater.py` (also the known legacy direct-worker identity used by
the reviewed signed bootstrap). SHA-256:
`6b1523a207ecff1ad7bff19c80d85809f4e453125bf5b5ecece64463465a6cad`.

Tests copy these actual historical bytes into a synthetic installation; they
never substitute a toy worker or relax the production accepted hash. The `.txt`
extension prevents accidental test discovery/execution. No live data is included.

## `updater-direct-0.5.txt`

Exact canonical-LF `deploy/updater.py` from commit
`a128c1709dc140f48b25a747a229f288bd751e60` (the reviewed protocol-2 direct worker
also shipped in app 0.5.0 and the manual transition package). SHA-256:
`22cd73dc810d08d90e1ec6af4e4b482bc60a0f42b44e1bf874c78682375264cd`.
It is a non-imported historical byte fixture for eligibility and reproducible
manual-bootstrap packaging, not a replacement for the current worker source.
