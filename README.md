# PCBridge

PCBridge is a local control plane for connecting AI agents to a user-owned Windows PC with explicit safety boundaries, durable task state, background execution, and human supervision.

## Current status

- Baseline runtime: `1.3.6-integrated-preview.1`
- Repository state: foundation/bootstrap
- Current implementation is an integrated preview and is preserved under `src/current/`.
- The vNext architecture is being specified before further feature work.
- No release claim is made from this repository yet.

## Product goals

1. Let an AI complete ordinary reversible work without repeated approval prompts.
2. Keep genuinely dangerous operations behind explicit HARDLOCK approval.
3. Preserve recovery paths, checkpoints, auditability, and fail-closed behavior.
4. Support reliable long-running agent handoff without duplicate work.
5. Allow human steering without unnecessarily breaking the agent loop.
6. Run in the background without stealing focus, keyboard, mouse, browser, or fullscreen applications.
7. Expose an optional compact monitoring/control surface for users who want visibility.

## Repository authority

- `docs/VNEXT_SPEC.md` — product/work specification.
- `docs/M1_AUTONOMOUS_OPERATION_SPEC.md` — canonical first implementation milestone: reliable autonomous multi-turn operation.
- `docs/ARCHITECTURE.md` — target architecture and component boundaries.
- `docs/PERMISSION_MODEL.md` — approval, risk and scope model.
- `docs/RELAY_RELIABILITY.md` — durable handoff/delivery model.
- `docs/HITL_AND_CONTROL_PLANE.md` — human supervision and operator UX.
- `docs/BENCHMARKS.md` — external references and adoption decisions.
- `docs/BASELINE.md` — what was imported from the current local build.

## Development rule

Do not weaken an acceptance criterion to declare a feature complete. A feature is complete only after implementation and the relevant real-environment verification pass.

## Security note

Authentication state, API keys, cookies, DPAPI blobs, local runtime state, generated binaries, logs and test artifacts must never be committed.

## License

No project-wide license has been selected yet. Third-party components retain their own licenses; see `THIRD_PARTY_NOTICES.txt`.
