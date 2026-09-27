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

## Agent-to-human request / notice channel

Agents also need a durable way to leave messages for the user without turning every message into an approval blocker.

Define an operator-facing `Human Attention Inbox` with structured message types:

- `INFO` — useful status or non-blocking notice.
- `ACTION_REQUIRED` — user must do something external, e.g. create an account, install/sign in to a service, connect a device, or provide a missing resource.
- `DECISION_REQUIRED` — user must choose between explicit options such as A/B.
- `QUESTION` — information is needed before a branch can continue.
- `WARNING` — important issue that does not itself require HARDLOCK approval.
- `APPROVAL_REQUIRED` — links to the existing HARDLOCK approval flow rather than duplicating it.

Each message should carry:
- task/run identity
- concise title
- short actionable body
- severity / urgency
- blocking vs non-blocking
- optional choices/actions
- created/read/resolved timestamps
- resolution result
- source agent/component

Examples:
- “GitHub account sign-in is required before publishing.”
- “Choose A or B before continuing this branch.”
- “The game must be closed before replacing this file.”
- “A browser re-authentication is required.”

The status/dashboard must surface unresolved messages until resolved. Completed messages move to history.

### Popup policy

Popup behavior is user-configurable.

Default:
- persist every message in the Human Attention Inbox
- do not steal focus
- do not interrupt fullscreen applications

Optional user settings:
- pop up blocking requests only
- pop up warnings + blocking requests
- pop up all agent notices
- notifications only / no popup
- quiet/fullscreen suppression with deferred notification

A popup is a presentation layer only. Closing or missing a popup must never lose the durable message.

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
