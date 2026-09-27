# PCBridge vNext — consolidated work specification

Status: FOUNDATION VERIFIED / IMPLEMENTATION GATED
Source of truth: this document for product requirements; subsystem details live in the linked docs.

## 1. Product intent

PCBridge should let an AI perform useful work on a user-owned PC with minimal approval friction while preserving explicit boundaries for genuinely dangerous actions. It must support long-running work, durable recovery, exact handoff between AI turns, human steering, and optional operator visibility without interfering with the user's foreground activity.

Quality and verified behavior take priority over accepting any particular implementation idea.

## 2. Current baseline

Live baseline at repository bootstrap: `1.3.6-integrated-preview.1`.

Already present:
- MCP/tunnel lifecycle
- Desktop Commander integration
- local approval UI
- DPAPI-protected local state
- non-HARDLOCK automatic Desktop Commander calls
- HARDLOCK classifiers
- task leases and audit history
- integration tests
- experimental relay

Known critical relay defect:
- immediate SendKeys targets current focus and can send to the wrong chat.
- treat this path as emergency-only until replaced.

See `BASELINE.md`.

## 3. Permission UX

### 3.1 Three execution outcomes
Every action resolves to:
- ALLOW
- ALLOW_WITH_CHECKPOINT
- HARDLOCK

Ordinary low-risk work should not prompt.

### 3.2 Reversibility first
A modification that is reliably reversible should normally execute automatically after any required checkpoint.

Examples:
- project source under Git
- mod files with snapshot/revision
- recoverable local DB/config changes

Irreversible or machine-/account-wide side effects raise risk.

### 3.3 HARDLOCK families
At minimum:
- payments/purchases/transfers
- credential/account/security-boundary changes
- security-disablement
- critical OS/boot/disk operations
- broad/irreversible deletion
- recovery-history destruction
- external authoritative irreversible actions

### 3.4 Task-scoped grants
The agent must preflight foreseeable A+B+C+D capabilities and request them together.

Once task A is approved, later turns/agents continuing A should reuse the same grant instead of re-prompting for equivalent scope.

Grant lifetime is explicit/configurable and revocable; it must not silently expire so aggressively that ordinary long-running work becomes approval spam.

### 3.5 Auto-expansion ceiling
Expose L0..L4 as defined in `PERMISSION_MODEL.md`.

Ceiling controls future scope expansion; it is not a simplistic “higher level inherits everything below it” privilege hierarchy.

### 3.6 Approval surface
Must support:
- risk label
- concise reason
- exact requested scope/capabilities
- rollback/checkpoint state
- allowed vs still blocked
- auto-expansion ceiling
- multi-select/bulk actions
- pending queue
- no stacked completed cards
- new request visible even if permissions window is already open

## 4. Risk engine

Inputs include:
- reversibility
- blast radius
- externality
- information sensitivity
- provenance
- authority/source-of-truth status
- replaceability
- control-surface impact
- recovery integrity

“Originally existed” vs “AI-created” is a useful provenance signal only; it is never sufficient by itself.

Examples:
- AI-created project code + Git rollback -> low/moderate
- AI-created live PCBridge permission engine -> high control-surface impact
- vendor executable -> vendor-managed, avoid patching when overlay/plugin is possible
- user-created unique planner data -> high preservation value
- server-authoritative game/account state -> external/authoritative

## 5. Recovery model

Before ALLOW_WITH_CHECKPOINT:
1. choose cheapest valid recovery primitive
2. create it
3. verify it exists/is readable
4. execute
5. verify expected outcome
6. preserve audit link from action to checkpoint

Protect Git history, backup, snapshot, audit and recovery stores from automatic destruction.

## 6. Background/non-interference

Default automation runs without:
- focus stealing
- synthetic input into active apps
- visible PowerShell/console windows
- mouse movement/clicks
- taking over the user's normal Chrome profile
- disrupting Netflix/video/fullscreen games
- unnecessary high resource pressure

PowerShell is diagnostic/fallback only and should run hidden when used.

Provide an optional mini status window for users who want monitoring. X hides monitor/main UI but must not terminate an active bridge. Tray owns lifecycle.

## 7. Exact durable relay

The normal relay must not use “current focus” as destination identity.

Required:
- exact conversation/provider identity
- durable outbox
- run_id + monotonic seq + unique message_id
- payload hash bound to idempotency key
- structural strong receipt
- ambiguity fence
- bounded backoff/jitter for transient/429 conditions
- auth and target mismatch fail closed
- single effective runner fencing
- handoff state survives bridge restart

Success means verified arrival in the intended conversation, not merely click success.

Primary implementation target:
- dedicated background browser/DOM provider
- semantic DOM locators and state waits
- no foreground-user interference
- authentication state treated as secret material

Parrot relay logic is a strong subsystem reference, not the whole runtime.

See `RELAY_RELIABILITY.md` and `BENCHMARKS.md`.

## 8. Baton handoff semantics

At the end of a completed atomic turn unit:
1. persist current durable task state
2. consume pending human-control messages
3. verify no HARDLOCK/ambiguity/recovery blocker
4. enqueue one successor handoff
5. deliver with idempotency key
6. verify strong receipt
7. fence/retire predecessor runner

If receipt is ambiguous, do not blindly send another CONTINUE.

The successor must check run_id/seq/message_id so duplicate delivery cannot create duplicate side effects.

## 9. Human supervisor channel

Provide an out-of-band control inbox:
- NOTE
- GUIDANCE
- COMMAND
- INTERRUPT
- APPROVAL

Normal steering is applied at safe atomic boundaries. INTERRUPT is immediate.

Every human input exposes acknowledgement:
- received
- read
- queued/applied
- rejected-with-reason

A user should be able to steer work without breaking the autonomous loop merely to get the agent's attention.

## 9.1 Agent-to-human attention

Agents may need to tell the user something without forcing a chat interruption.

Provide a durable Human Attention Inbox for:
- informational notices
- external action required
- decision required (including explicit A/B choices)
- questions
- warnings
- approval-required links

Every notice remains visible in status/control surfaces until read/resolved and is never dependent on a transient popup.

Popup/desktop notification behavior is user-configurable. When enabled, PCBridge may surface new attention items, but normal automation must still avoid focus stealing and fullscreen/game interruption. Popup dismissal does not resolve the underlying item.

## 10. Agent Control Plane

Dashboard is functional, not decorative.

Required views:
- active tasks/agents and current step
- last activity/heartbeat
- pending attention
- approvals/HARDLOCK
- human-control inbox
- relay/handoff health
- checkpoints/recovery readiness
- bridge/browser health
- recent history/errors/cooldowns

Future orchestration can support multiple workers/agents, but do not add graph complexity before durable single-task semantics are correct.

## 11. Main/tray UX

- Connect button reflects Connect -> Connecting -> Connected/Error.
- Closing visible window while connected minimizes/hides to tray.
- Tray: Open status, Open approvals, Pause, Disconnect, Exit.
- Approval broker continues while main window is hidden.
- Permission window being open must not suppress new HARDLOCK attention.
- Obsolete “share/save EXE” UX is removed from primary UI.

## 12. Observability

Use structured events with:
- run_id
- task_id
- atomic_unit_id
- attempt
- tool/action
- policy decision
- checkpoint_id
- relay message_id
- human-message id
- timestamps and terminal state

OpenTelemetry-style trace/span propagation is the preferred model.

Do not log credentials, cookies, auth state or arbitrary sensitive content by default.

## 13. Error taxonomy

Handle differently:
- transient/network/429 -> bounded retry/backoff
- agent-recoverable -> surface error to agent and re-plan
- human-fixable -> supervisor/interrupt path
- permission blocked -> HARDLOCK queue
- auth expired -> reauth-required
- ambiguous side effect -> reconciliation/fence
- unexpected invariant failure -> stop affected branch and preserve diagnostics

Repeated identical retry without correcting the failure cause is not an acceptable recovery strategy.

## 14. Repository/build discipline

- GitHub repo is canonical: https://github.com/amzsdq/PCBridge
- never commit secrets/auth/browser state/generated runtime state
- preserve exact current baseline before refactoring
- replace one-off patch scripts with a reproducible build/test/install pipeline
- version source, tests and acceptance evidence together
- no “complete” claim without real-environment validation

## 15. Acceptance gates

### Permission
- ordinary reversible Warhammer/mod development proceeds without repeated approval prompts
- HARDLOCK test cases still stop for explicit approval
- bundled scope request avoids serial A/B/C/D prompts
- multi-select approval/revoke works
- open permissions window still surfaces new HARDLOCK request

### Relay
- message reaches exact bound conversation while another chat is active
- no wrong-chat delivery
- works without the user's normal Chrome window being open, if the selected provider supports the authenticated session
- does not steal focus during typing/video/game
- duplicate attempt with same message_id does not create duplicate work
- ambiguous receipt never triggers blind resend
- 429/transient errors back off and recover
- auth expiry stops safely

### HITL
- NOTE/GUIDANCE can be submitted while work continues
- COMMAND applies at boundary
- INTERRUPT stops promptly with resumable state
- UI shows acknowledgement

### Lifecycle
- X hides but does not kill active service
- tray exit really stops service
- restart preserves durable task/relay state and does not replay uncertain side effects

### Recovery
- checkpoint creation is verified before risky reversible action
- rollback path is exercised in tests
- recovery-store destruction remains HARDLOCK

## 16. Implementation milestones

M0 — repository/spec/benchmarks/baseline foundation. DONE.

M1 — Autonomous Operation Ready. This is the first implementation milestone and must be completed before broader feature work. It combines exact durable relay, successor-turn startup, duplicate fencing, transient-error recovery, restart safety, foreground-user non-interference, and continued use of the current non-HARDLOCK-auto execution path.

M1 acceptance requires a real task to cross multiple consecutive ChatGPT baton handoffs without manual user prompting, without wrong-chat delivery, duplicate work, visible console/PowerShell dependency, focus stealing, keyboard/mouse interference, or unsafe blind retries.

M2 — unify legacy scoped-task permission path with ALLOW/CHECKPOINT/HARDLOCK, task grants and automatic checkpoints.

M3 — approval broker lifecycle, multi-select/bulk controls, queued attention and tray behavior.

M4 — human supervisor inbox, mini monitor and dashboard/control-plane status model.

M5 — durable task runtime, recovery adapters and structured observability.

M6 — advanced multi-agent orchestration and workflow visualization.

Do not begin M2+ implementation work until M1 is either completed or explicitly reprioritized by the user.
