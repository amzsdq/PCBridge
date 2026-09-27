# Human supervision and control plane

## Human-in-the-loop objective

Human input should be first-class without turning every ordinary intervention into a hard stop or breaking the long-running relay loop.

Use an out-of-band supervisor inbox.

## Message classes

- NOTE — record for later; do not interrupt current atomic work
- GUIDANCE — apply at the next safe boundary
- COMMAND — reprioritize after the current atomic unit unless unsafe to wait
- INTERRUPT — stop immediately and preserve resumable state
- APPROVAL — resolve a blocked HARDLOCK branch

Each operator message has acknowledgement states such as:
`RECEIVED -> READ -> APPLIED / QUEUED / REJECTED_WITH_REASON`.

The UI should show those states so the user does not need to ask whether an instruction was noticed.

## Safe-boundary consumption

Between atomic units:
1. checkpoint task state
2. consume supervisor inbox
3. merge new constraints/priorities
4. re-plan if necessary
5. continue

An emergency INTERRUPT bypasses the normal boundary and stops side effects immediately.

## Control-plane dashboard

Group by function rather than decorative layout.

Suggested sections:
- Active work: task, phase, last activity, current agent
- Attention: HARDLOCK, ambiguity, auth, errors, cooldown
- Human inbox: queued steering and acknowledgement
- Relay: target, last receipt, next baton
- Recovery: latest checkpoint and rollback readiness
- Bridge/system: connection, version, health, resource pressure
- History: searchable completed/failed activity

## Mini monitor

Optional compact window:
- current task
- current step
- last activity age
- pending approvals
- health
- pause/stop/open-dashboard controls

Behavior:
- X hides monitor only; work continues
- tray restores it
- optional always-on-top
- automatically hide/suppress overlays during fullscreen applications if configured

## Tray / lifecycle

When bridge is connected, closing the main window must not silently kill the bridge or approval broker.

Tray menu should include at least:
- Open status
- Open approvals
- Pause automation
- Disconnect
- Exit PCBridge

Connection button should reflect state: Connect -> Connecting -> Connected / Error.

Obsolete “save/share EXE” UI is not a primary product feature.

## Foreground-user invariant

Watching Netflix, gaming, typing, or using another browser tab must not be disturbed by normal background automation. Any fallback that needs foreground input must require explicit opt-in for that operation.
