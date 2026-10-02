# Target architecture

## Design objective

PCBridge is a local agent control plane, not merely a remote shell. It should coordinate execution, permissions, recovery, durable handoff, human steering and operator visibility while minimizing interference with the foreground user session.

## Major components

### 1. Bridge Core
Owns process lifecycle, local IPC, MCP exposure, secure state, health/readiness and version/cutover.

### 2. Execution Adapter
Desktop Commander remains the primary file/process primitive provider. PCBridge wraps it with policy, audit, checkpoints and result retention.

### 3. Policy Engine
Classifies each planned action as:
- ALLOW
- ALLOW_WITH_CHECKPOINT
- HARDLOCK

The engine evaluates reversibility, blast radius, externality, authority/source-of-truth status, replaceability, control-surface impact and existing information-sensitivity policy.

### 4. Checkpoint / Recovery Service
Creates the cheapest valid rollback primitive before reversible-but-risky work: Git revision, file snapshot, DB transaction/export, config backup, or application-specific checkpoint.

Recovery infrastructure is itself protected.

### 5. Durable Task Runtime
Tracks task/run identity, atomic work units, state transitions, retries, checkpoints, errors, operator input and handoffs. State must survive bridge restarts.

### 6. Provider Adapter
Provider-neutral boundary between PCBridge durable task state and a specific AI conversation surface.

M1 implements ChatGPT only. The interface must still separate:
- target identity/navigation
- composer readiness
- send authorization/dispatch
- delivery receipt
- response ownership/terminal evidence
- provider error classification

Future Claude/Gemini adapters may implement the same contract without changing Policy Engine, checkpoints, task state, HITL or Desktop Commander execution.

The adapter never owns permission policy or durable run truth.

### 7. Relay Core
Durably hands work from one ChatGPT turn/agent to the next. It owns target identity, outbox, idempotency keys, strong receipts, ambiguity fencing, backoff and retry policy.

### 8. Browser Provider
Provider boundary for ChatGPT delivery.
Preferred order:
1. dedicated background browser/DOM provider
2. existing authenticated browser extension/provider
3. browser UI automation only when target identity is provable
4. foreground SendKeys/coordinates only as explicit emergency fallback

A normal background task must never steal user focus, mouse or keyboard.

### 9. Human Supervisor Channel
Out-of-band inbox for NOTE, GUIDANCE, COMMAND, INTERRUPT and APPROVAL messages. Ordinary steering is consumed at safe atomic boundaries; emergency interrupt is immediate.

### 10. Agent Control Plane UI
Dashboard + optional mini status window + tray lifecycle. Shows active tasks, pending approvals, recent activity, relay health, checkpoints and operator-message acknowledgement.

### 11. Observability
Every task has stable `run_id`; every atomic unit/tool call gets trace/span identity. Logs should be structured and privacy-bounded.

## Non-interference invariant

Default background mode must not:
- bring windows to foreground
- synthesize input into the user's active application
- display PowerShell/console windows
- take over the user's Chrome profile
- interrupt video playback or fullscreen games
- create avoidable CPU/GPU/disk spikes

The optional monitor UI is opt-in and independently hideable without stopping work.

## Fail-closed invariants

- Unknown target conversation: do not send.
- Ambiguous send outcome: do not auto-send a duplicate.
- Lost recovery guarantee: stop before destructive continuation.
- HARDLOCK: no automatic scope expansion.
- Authentication expired: surface reauth-required; do not bypass.
