# Roadmap and verification gates

## F0 — Repository and architecture foundation
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

## F1 — Durable exact relay
Status: PENDING

Build:
- provider abstraction
- durable outbox/inbox identity
- idempotency and fencing
- strong receipt / ambiguous state
- background DOM provider
- transient/429/auth/target-mismatch recovery

Verify in real authenticated ChatGPT:
- exact target with other chats active
- normal Chrome closed where supported
- zero focus/input interference
- duplicate/ambiguous cases
- restart recovery

## F2 — Permission engine unification
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

## F3 — Approval broker and lifecycle UX
Status: PENDING

Build:
- queued single approval surface
- multi-select/bulk actions
- risk labels and rollback summary
- foreground attention even when permissions window already exists
- tray lifecycle
- remove obsolete share-EXE surface
- connection state machine UI

## F4 — Human supervisor / operator control
Status: PENDING

Build:
- NOTE/GUIDANCE/COMMAND/INTERRUPT/APPROVAL inbox
- acknowledgement lifecycle
- safe-boundary consumption
- mini monitor
- dashboard active/attention/history views

## F5 — Durable task runtime / observability
Status: PENDING

Build:
- atomic-unit state machine
- structured event history
- checkpoint links
- trace/span propagation
- restart-safe execution ownership

## F6 — Advanced orchestration
Status: DEFERRED

Only after F1-F5 are verified:
- multiple agents/workers
- workflow canvas/graph
- richer scheduling/dependencies
- cross-agent routing

## Completion rule

A milestone is not DONE because code compiles or static tests pass. Real-environment criteria must pass where the feature depends on Windows UI, authenticated ChatGPT, browser behavior, restart semantics or approval UX.
