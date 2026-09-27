# Permission and risk model

## User-facing decision classes

### ALLOW
Execute without approval.

Examples: reads, inspection, ordinary reversible project-local operations with negligible blast radius.

### ALLOW_WITH_CHECKPOINT
Create/verify a rollback point automatically, then execute without user approval.

Examples: editable project source, mod files, local databases/configuration where rollback is reliable.

### HARDLOCK
Explicit local human approval is required.

Initial HARDLOCK families:
- payment, purchase, transfer or financial commitment
- credential/account/security-boundary changes
- disabling security controls
- machine-critical OS/boot/disk operations
- broad or plausibly irreversible deletion
- destroying Git history, backups, snapshots, audit or recovery state
- external authoritative actions whose effects cannot be reliably reversed

## Risk dimensions

Risk is not a single military-style privilege number. Evaluate:
- reversibility
- blast radius
- external side effects
- information sensitivity
- authority / source-of-truth status
- replaceability
- provenance
- control-surface impact
- recovery integrity

Provenance is a modifier, not a safety verdict. AI-created code can still be high-risk if it controls the whole machine; vendor files can be replaceable yet operationally sensitive.

## Grants

A user approval should be task-oriented, not call-oriented.

Before work starts, the agent should preflight the whole plan and request A+B+C+D together when reasonably foreseeable.

A grant records:
- task identity and purpose
- exact capabilities/scopes
- policy ceiling
- creation/expiry/revocation state
- audit trail

Repeated execution of the same approved task scope should not ask again merely because a new chat/agent turn begins.

## Auto-expansion ceiling

- L0 — ask on every scope expansion
- L1 — auto-expand only for clearly reversible operations
- L2 — auto-expand within the declared task boundary (recommended default)
- L3 — auto-expand everything except Critical/HARDLOCK families
- L4 — unrestricted expansion, explicit opt-in only

The ceiling limits automatic expansion. It does not mean that a numerically “higher” permission automatically grants every unrelated lower-level capability.

## Approval UX

Approval surfaces must show concise:
- requested task
- risk label, e.g. PAYMENT / SECURITY / DELETION / RECOVERY
- what will be allowed
- what remains blocked
- rollback/checkpoint state
- auto-expansion ceiling

Requirements:
- multi-select and bulk approve/revoke/policy changes
- one request for predictable multi-step scope, not serial A/B/C/D prompts
- an already-open permissions window must still surface new HARDLOCK requests
- popup queue rather than stacked windows
- completed requests move to history instead of accumulating in the main list

## Current compatibility

The current v1.3.6 Desktop Commander path runs non-HARDLOCK work automatically. The legacy scoped-task API remains to be unified with this model.
