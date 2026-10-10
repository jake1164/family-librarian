# noVNC (vendored)

- Project: https://github.com/novnc/noVNC
- Version: v1.7.0
- Source: https://github.com/novnc/noVNC/archive/refs/tags/v1.7.0.tar.gz
- SHA-256: b1003a11b6e6e8d8f7f5e5586daae7f8ca651d8aee0aa155ff9ac841c48f52c6
- License: see `LICENSE.txt` (noVNC is MPL-2.0; bundled `vendor/pako` is MIT/zlib)

Only `core/` and `vendor/` are vendored here (the parts `core/rfb.js` needs
as an ES module) -- not the full repository, not `app/` or `vnc_lite.html`.

This is the same pinned release FLP-Annas vendors for its own admin-side
noVNC client (`FLP-Annas` repo, `Dockerfile`'s `NOVNC_VERSION`/`NOVNC_SHA256`
build args) -- kept in sync intentionally so both ends of a future brokered
session (HUMAN-ACQ-1) run the same RFB client version.

Do not hand-edit anything under `core/`/`vendor/`; replace the whole
directory when bumping the pinned version, and update the version/checksum
above.
