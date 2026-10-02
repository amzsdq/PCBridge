# Chat On Steroids provenance

Upstream: https://github.com/totec448-spec/chat-on-steroids
Pinned review commit: `a1879601684712cbc3d6100ee4fe2dacbdf1b7b4`
Reviewed release: `v2.1.25`
License: MIT (see `LICENSE` in this directory)

## PCBridge use

M1 uses Chat On Steroids as a selective implementation reference for browser/session reliability.

High-value upstream areas:
- `src/main/session/input.ts`
- `src/main/session/correlation.ts`
- `src/main/session/continuation.ts`
- `src/main/bridge.ts`
- `extension/background.js`
- `extension/content.js`
- `extension/chatgpt-dom.js`
- `extension/browser-control.js`

No whole-file source has been copied into PCBridge as of this provenance record.

If code is later ported or derived, record the exact upstream file/function and pinned commit here before merging.

## Incident fixtures to preserve

- #744 / PR #752 — composer remount between draft insertion and Send.
- #746 — continuation ownership/lifecycle failures.
- PR #755 — stale earlier final must not close a newly started response turn.

PCBridge acceptance tests should reproduce these failure classes independently rather than assuming the upstream fix applies unchanged.
