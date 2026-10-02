# Roadmap and verification gates

## M0 — Repository and architecture foundation
Status: DONE

Exit criteria:
- current source baseline preserved in GitHub
- secret/auth/generated artifacts excluded
- consolidated vNext spec exists
- permission, relay, HITL/control-plane architecture documented
- Parrot reviewed critically
- distributed-systems/browser/HITL benchmarks recorded
- repository contents re-read from GitHub to verify writes

Verification: exact text equality confirmed for the five imported core C# baseline files after GitHub upload.

## M1 — Autonomous Operation Ready
Status: SPEC READY / IMPLEMENTATION NOT STARTED

Detailed specification: `M1_AUTONOMOUS_OPERATION_SPEC.md`
Research review: `research/COS_PARROT_M1_REVIEW_2026-10-02.md`

Objective:
A user can give one instruction and PCBridge can continue useful work across multiple ChatGPT turns without manual baton-passing, while preserving exact targeting, duplicate safety, approval boundaries, restart recovery and foreground-user non-interference.

Build:
- exact-target browser/provider abstraction
- durable relay outbox/inbox
- run_id + monotonic seq + message_id
- idempotent successor consumption and runner fencing
- strong receipt / ambiguous-delivery reconciliation
- transient/429/loading-failure backoff and recovery
- background DOM provider that does not depend on the user's active Chrome tab
- restart-safe pending handoff state
- use the current non-HARDLOCK-auto path so ordinary development work can continue without repeated approval

Verify in real authenticated ChatGPT:
- current turn completes and wakes the exact bound conversation
- successor turn actually starts and continues the same run
- repeated/duplicate delivery cannot create duplicate side effects
- ambiguous delivery is reconciled instead of blindly resent
- another ChatGPT conversation may be active without receiving the handoff
- user Chrome may be closed where the selected provider supports its own authenticated session
- typing, video playback and fullscreen games are not disturbed
- no foreground focus, keyboard, mouse or visible PowerShell/console dependency
- transient network/loading failures and 429 conditions recover with bounded backoff
- bridge restart preserves pending handoff without unsafe replay
- HARDLOCK still stops for explicit user approval
- demonstrate multiple consecutive autonomous baton passes in one real task

## M2 — Permission engine unification
Status: PENDING

Build:
- ALLOW / ALLOW_WITH_CHECKPOINT / HARDLOCK
- task grants and scope bundling
- L0..L4 auto-expansion ceiling
- provenance/authority/replaceability/control-surface modifiers
- legacy task API migration

Verify:
- ordinary development work is prompt-free
- HARDLOCK families remain blocked
- recovery integrity cannot be auto-destroyed

## M3 — Approval broker and lifecycle UX
Status: PENDING

Build:
- queued single approval surface
- multi-select/bulk actions
- risk labels and rollback summary
- foreground attention even when permissions window already exists
- tray lifecycle
- remove obsolete share-EXE surface
- connection state machine UI

## M4 — Human supervisor / operator control
Status: PENDING

Build:
- NOTE/GUIDANCE/COMMAND/INTERRUPT/APPROVAL inbox
- acknowledgement lifecycle
- safe-boundary consumption
- mini monitor
- dashboard active/attention/history views

## M5 — Durable task runtime / observability
Status: PENDING

Build:
- atomic-unit state machine
- structured event history
- checkpoint links
- trace/span propagation
- restart-safe execution ownership

## M6 — Advanced orchestration
Status: DEFERRED

Only after M1-M5 are verified:
- multiple agents/workers
- workflow canvas/graph
- richer scheduling/dependencies
- cross-agent routing

## Completion rule

A milestone is not DONE because code compiles or static tests pass. Real-environment criteria must pass where the feature depends on Windows UI, authenticated ChatGPT, browser behavior, restart semantics or approval UX.
