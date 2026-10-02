# M1 — Autonomous Operation Ready

Status: SPEC READY / IMPLEMENTATION NOT STARTED
Date: 2026-10-02
Parent: `VNEXT_SPEC.md`
Research basis: `research/COS_PARROT_M1_REVIEW_2026-10-02.md`

## 1. Milestone goal

A user gives one task instruction. PCBridge then allows the same ChatGPT conversation to continue useful work across multiple assistant turns without manual “continue” messages, while:

- never sending to the wrong conversation
- never blindly duplicating a possibly delivered handoff
- preserving work across PCBridge/browser restart
- respecting HARDLOCK and user intervention
- not stealing focus, keyboard, mouse, browser tabs or fullscreen applications
- not requiring visible PowerShell/CMD windows
- continuing to use the current non-HARDLOCK-auto execution path for ordinary work

M1 is the first implementation milestone because every later long-running PCBridge task becomes easier and more testable once reliable self-handoff exists.

## 2. Non-goals

M1 does NOT include:
- full M2 permission-model unification
- approval-dashboard redesign
- multi-agent/swarm scheduling
- Claude/Gemini provider adapters
- mobile remote control
- full Compact & Resume/new-chat context migration
- general browser-control product features
- replacing Desktop Commander
- a second LLM/OpenRouter “Goal” decision engine

Do not expand M1 merely because a reference project contains those features.

## 3. Core architecture

```text
ChatGPT turn
   |
   | MCP: automation_handoff / automation_complete
   v
PCBridge M1 Coordinator
   |-- durable Run State
   |-- durable Outbox
   |-- fencing generation
   |-- retry/backoff
   |-- Human Attention / HARDLOCK wait projection
   |
   v
ChatGPT Provider Adapter
   |
   v
PCBridge-owned browser profile
   |-- exact conversation
   |-- DOM/content companion
   |-- send/receipt observation
   |-- response-turn evidence
   |
   v
ChatGPT
```

Desktop Commander remains separate and continues to execute normal PC work.

## 4. Authority rules

### 4.1 PCBridge Core owns semantic truth
The browser/extension reports evidence only.

Core is authoritative for:
- active run
- run generation/fencing token
- target provider/conversation
- pending handoff
- delivery state
- retry state
- terminal completion
- blocked/human-attention state

### 4.2 Exact identity
Every M1 operation carries:
- `run_id`
- `generation`
- `seq`
- `message_id`
- `payload_hash`
- `provider`
- `provider_profile_id`
- `conversation_id`
- browser `document_id/navigation_epoch` when a page is leased

Unknown identity fails closed.

### 4.3 One effective runner
Each run has a monotonically increasing `generation`.

A side effect is valid only while its generation is current.

A stale predecessor that wakes after restart/recovery may read state but must be rejected before new side effects.

## 5. Agent-facing tool contract

### 5.1 `automation_handoff`
Purpose: declare that the current turn is nearing its useful boundary but the requested job is not complete.

Input concept:
```text
run_id
summary          concise verified work done this turn
next_action      concrete next work unit
verification     what was actually checked
blocking_state?  none | hardlock | human | external
```

Behavior:
1. validate current run ownership
2. atomically persist current task state + one outbound handoff intent
3. return a durable handoff id
4. DO NOT send while the current assistant response is still active
5. browser coordinator waits for the exact current response to terminal-confirm
6. then it attempts the queued handoff

Repeated calls for the same `run_id/generation/seq` return the existing handoff record rather than creating another.

### 5.2 `automation_complete`
Purpose: declare that the whole user-requested task is complete and verified.

Behavior:
- persist terminal run state
- cancel any unsent automatic baton for that generation
- do not create another ChatGPT turn

Completion must include verification evidence. “I think it is done” is not a valid automatic terminal criterion.

### 5.3 Connector instruction
When an autonomous run is active:
- if requested work remains and the current turn is about to end, call `automation_handoff` exactly once
- if all requested work is implemented and verified, call `automation_complete`
- HARDLOCK/human/external blockers are not completion
- do not create progress-only handoffs while useful work remains inside the current turn

This replaces the need for an M1 second-model Goal engine.

## 6. Durable state model

M1 should initially keep one authoritative atomic state document rather than split semantically coupled state across several files.

Reason:
- PCBridge already has atomic temp-write + flush + replace
- one document avoids a cross-file dual-write window
- state volume is small in M1
- a database can replace this later without changing the state machine

Conceptual schema:

```text
AutomationStateV1
  schema_version
  runs[]
    run_id
    target
      provider
      provider_profile_id
      conversation_id
    generation
    seq
    status
    predecessor_message_id
    current_turn
    pending_handoff?
    retry?
    attention?
    updated_utc

Handoff
  message_id
  seq
  payload_hash
  payload
  state
  created_utc
  send_authorized_utc?
  dispatched_utc?
  receipt_utc?
  response_turn_id?
  terminal_utc?
  attempt
  next_attempt_utc?
  error_class?
```

The state file is credential-adjacent local state and should be DPAPI protected.

## 7. Handoff state machine

Normal path:

```text
HANDOFF_COMMITTED
  -> WAIT_CURRENT_TURN_END
  -> TARGET_READY
  -> COMPOSER_CLAIMED
  -> SEND_AUTHORIZED
  -> SEND_DISPATCHED
  -> USER_RECEIPT_CONFIRMED
  -> RESPONSE_BINDING
  -> TURN_RUNNING
  -> TERMINAL_OBSERVED
  -> TERMINAL_CONFIRMED
  -> NEXT_DECISION
```

Exception states:

```text
PRE_SEND_RETRY
RATE_LIMITED
LOAD_RECOVERY
BLOCKED_AUTH
TARGET_MISMATCH
AMBIGUOUS
WAITING_HARDLOCK
WAITING_HUMAN
CANCELLED
FAILED_TERMINAL
```

A state transition that grants permission for an external side effect must be durably committed before the side effect.

## 8. Browser provider

### 8.1 Selected M1 provider model
Use a PCBridge-owned Chrome/Edge profile and minimal companion.

Requirements:
- separate `user-data-dir`
- separate PCBridge provider profile id
- exact profile never reused as the user’s ordinary browsing profile
- PCBridge launches it when needed
- authenticated session persists locally
- browser can be absent before the run
- no dependence on whichever user browser/tab currently has focus

Selected runtime policy:
1. explicitly configured companion-capable Chromium runtime
2. PCBridge-managed Chrome for Testing / compatible Chromium runtime
3. otherwise Human Attention: browser runtime setup required

Do **not** silently fall back to the user's normal installed Chrome/Edge. Chrome removed `--load-extension` from branded builds in Chrome 137 and `--disable-extensions-except` from branded builds in Chrome 139, so relying on those flags against the user's ordinary browser is both unreliable and contrary to the non-interference goal.

The M1 provider should use a PCBridge-owned testing/runtime browser with a dedicated profile. Download/install of that runtime is a separate reversible setup action and should not occur silently while the user is gaming or otherwise bandwidth-sensitive.

### 8.2 Background/non-interference
Normal provider operation must not:
- activate a browser window
- move the user cursor
- synthesize OS keyboard input
- paste through clipboard
- minimize/close the user’s applications
- navigate the user’s ordinary browser
- show PowerShell/CMD
- overlay fullscreen games

The dedicated browser may run minimized/hidden only if live tests prove ChatGPT/extension behavior remains correct.

If a provider implementation cannot meet this rule, it does not pass M1.

### 8.3 Browser companion responsibilities
The companion may:
- identify the exact visible ChatGPT conversation inside its own provider profile
- expose stable structural DOM observations
- lease the composer
- input the exact PCBridge-authored handoff
- request native Send
- observe exact user-message receipt
- observe response start/running/terminal evidence
- report provider errors/loading/auth state

It may NOT:
- own durable run state
- choose a different conversation
- auto-resend an ambiguous handoff
- make HARDLOCK decisions
- execute local PC tools

## 9. Exact target rules

Before any text insertion or Send:
1. page origin is an allowed ChatGPT origin
2. URL structurally contains the exact expected conversation id
3. Project/custom route normalization is validated
4. content document/navigation epoch is current
5. no superseded/hidden old conversation is being treated as the active target
6. current run generation is still valid

Failure -> `TARGET_MISMATCH` or re-navigation. Never “closest matching chat.”

## 10. Composer lease rules

A composer lease binds:
- conversation id
- document/navigation epoch
- composer node identity
- exact payload hash
- attachment count = 0 for M1 automatic baton
- pre-Send state

Safe one-time rebind is allowed only if:
- Send has not been authorized
- exact target is unchanged
- exact payload hash is unchanged
- no user/manual draft exists in the dedicated provider profile
- no attachment exists
- replacement composer is structurally valid

After `SEND_AUTHORIZED`, a disappearing/replaced editor is not automatically rebound for a second Send.

## 11. Delivery receipt

Never mark delivered based on:
- click success
- Enter key dispatch
- text disappearing from the composer
- URL staying the same

Strong receipt requires structural evidence attributable to the exact send, preferably:
- exact new user-message id anchored after Send, or
- a verified user-message count increase plus exact text/hash/target evidence,
AND
- target conversation/document ownership still matches

Assistant generation starting may strengthen the receipt but should not substitute for a mismatched user-message identity when a stronger provider signal is available.

## 12. Ambiguity rule

If Send may have happened but strong receipt is not confirmed:

`SEND_DISPATCHED -> AMBIGUOUS`

Then:
- persist ambiguity immediately
- forbid automatic resend of the same message_id
- reload/re-observe the exact target if safe
- search for evidence that the same handoff was accepted
- if found -> `USER_RECEIPT_CONFIRMED`
- if non-delivery is proven -> return to a retryable pre-send state
- if unresolved -> Human Attention

A timer expiring is never proof of non-delivery.

## 13. Response lease / terminal ownership

After user receipt:
1. bind the next response to that exact user message
2. persist `response_turn_id` or equivalent provider evidence
3. ignore finals/terminals that predate or belong to another user-message branch
4. treat browser terminal observations as provisional
5. only `TERMINAL_CONFIRMED` releases response ownership

This specifically guards the CoS #755/#746 failure class where an earlier final reappears and closes a newly started turn.

## 14. Human input priority

If the user sends new guidance through ChatGPT or the PCBridge supervisor channel:

### Before SEND_AUTHORIZED
- user input wins
- cancel/supersede the automatic baton
- update durable task state
- next handoff is rebuilt from the new instruction

### After SEND_DISPATCHED
- do not race a second message
- reconcile the dispatched baton
- queue user guidance behind the confirmed/ambiguous delivery state
- INTERRUPT fences future work immediately

## 15. HARDLOCK interaction

If work reaches HARDLOCK:
- do not send endless “continue” messages
- run state becomes `WAITING_HARDLOCK`
- create one durable Human Attention item
- resume only after the approval result is durably applied

Ordinary non-HARDLOCK work continues under the current live v1.3.6 policy until M2 replaces it.

## 16. Provider error recovery

### 16.1 429 / rate limit
- never bypass limits or switch identities to evade them
- use provider retry hint when structurally available
- otherwise bounded exponential backoff + jitter
- suggested default schedule: 2m, 5m, 10m, 20m, 40m, max 60m
- verify run generation and target before every retry

### 16.2 Conversation/loading failure before Send
- reload exact target
- revalidate identity
- if repeated, restart the dedicated provider browser
- preserve the same handoff/message_id
- no OS foreground interaction

### 16.3 Auth expired / CAPTCHA / interactive challenge
- `BLOCKED_AUTH`
- Human Attention: sign-in/re-auth required
- no credential scraping or bypass

### 16.4 Browser crash
- restart dedicated profile
- reload durable coordinator state
- reconcile any `SEND_DISPATCHED`/AMBIGUOUS record before a new Send

### 16.5 Provider refusal/restriction
- do not route around it
- surface terminal/blocking provider state

## 17. Restart semantics

On PCBridge startup:
1. load durable AutomationState
2. invalidate ephemeral browser document/composer leases
3. retain run generation
4. inspect every nonterminal handoff
5. never replay `SEND_DISPATCHED` blindly
6. reconcile exact target first
7. resume only states with a safe transition
8. start dedicated browser only when a pending run needs it

On browser restart:
- extension/browser storage is treated as cache/custody evidence
- Core durable state wins
- new browser/document epoch must be acquired

## 18. Resource/non-interference requirements

M1 acceptance includes resource behavior.

Default:
- provider process priority at or below normal; evaluate BELOW_NORMAL after live tests
- no aggressive polling; event-driven bridge + bounded maintenance polling
- no screenshot loop
- no full DOM serialization loop
- extension service worker may sleep; state survives in Core
- notifications suppressed during fullscreen unless user policy says otherwise

CPU/GPU limits are measured, not guessed. Do not disable GPU or animation if that breaks provider correctness.

## 19. Upstream reuse plan

### 19.1 CoS code candidates for selective port/adaptation
High-value references:
- `session/input.ts` — outbox/ambiguity/idempotent send ownership
- `session/correlation.ts` — exact ownership correlation
- `session/continuation.ts` — state-transfer/continuation transaction ideas
- `bridge.ts` — browser command custody/receipts
- `extension/background.js` — browser-lifetime journal/dispatch
- `extension/content.js` + `chatgpt-dom.js` — ChatGPT turn/composer/DOM evidence
- `extension/browser-control.js` — background debugger/DOM patterns
- `control-api.ts` — later local-control security reference

Do not copy whole files automatically. Extract only the dependency-minimal logic needed by M1.

### 19.2 Provenance
Before the first copied code lands:
- create `third_party/chat-on-steroids/README.md`
- include upstream URL, commit and MIT license
- record source path for copied/derived modules
- update `THIRD_PARTY_NOTICES.txt`

## 20. Implementation units

### M1.0 — Upstream/provenance fixture
- pin CoS commit and Parrot reference commit
- add third-party notice scaffolding
- capture relevant upstream tests/bug cases as PCBridge acceptance fixtures
- no live behavior change

### M1.1 — Durable coordinator core
- AutomationStateV1
- atomic persistence
- run generation/fencing
- message id/sequence/payload hash
- handoff/complete tool surface
- unit tests for restart and duplicate calls

### M1.2 — Provider process/profile manager
- discover Chrome/Edge
- dedicated user-data-dir
- launch without stealing focus
- health/identity
- Human Attention for first login/auth expiry
- no ChatGPT send yet

### M1.3 — Minimal ChatGPT browser companion
- exact conversation parser
- document/navigation epoch
- composer lease
- safe pre-Send remount rebind
- native send
- strong receipt
- response-turn ownership
- tests for stale final/remount/hidden Project chat

### M1.4 — Coordinator/provider integration
- state machine end-to-end
- current turn terminal gate
- dispatch after terminal
- exact receipt -> response lease -> terminal confirm
- duplicate fencing

### M1.5 — Recovery/backpressure
- 429
- offline/network
- conversation load failure
- browser crash
- auth expiry
- ambiguous delivery reconciliation

### M1.6 — Non-interference hardening
- no OS SendKeys
- no clipboard send
- no user browser mutation
- no visible console
- fullscreen/game/browser coexistence tests
- bounded polling/resource measurements

### M1.7 — Live acceptance / dogfood
Run a real authenticated ChatGPT task that crosses repeated autonomous handoffs and produces durable evidence.

Only after M1.7 passes may M1 be marked DONE.

## 21. Required tests

### Deterministic/static
- duplicate `automation_handoff` returns same message_id
- stale generation rejected
- state survives process restart
- task-state + handoff intent cannot diverge
- target mismatch cannot Send
- pre-Send composer remount can rebind once
- post-authorization remount cannot auto-resend
- ambiguous state fences retry
- old terminal cannot close new response lease
- human message supersedes pre-Send baton
- HARDLOCK produces one wait/attention state, not a continuation loop

### Fault injection
- kill PCBridge after HANDOFF_COMMITTED
- kill browser before Send
- kill browser immediately after Send dispatch
- drop browser ACK
- reload page between type and Send
- remount composer between type and Send
- inject stale previous final after new turn starts
- network offline/online
- simulated 429
- simulated auth loss

### Real environment
- target this exact chat while another ChatGPT chat is foreground
- user actively types in another application during handoff
- user browses/watches video during handoff
- fullscreen game active during handoff
- normal Chrome closed
- normal Chrome open on unrelated profile/tabs
- dedicated browser restarts
- PCBridge restarts
- Project chat route if available

## 22. M1 release gate

M1 is DONE only when all are true:

1. At least **10 consecutive autonomous baton handoffs** succeed in one real authenticated task.
2. Zero wrong-conversation deliveries in the acceptance campaign.
3. Duplicate/fault-injection cases produce zero duplicate successor side effects.
4. At least one ambiguous-send test proves auto-resend is fenced.
5. Restart after a committed handoff resumes safely.
6. Another chat can be active without receiving the baton.
7. Normal Chrome can be closed before a continuation and the dedicated provider recovers.
8. User keyboard/mouse focus is never stolen in the non-interference run.
9. No visible PowerShell/CMD dependency.
10. HARDLOCK still pauses rather than being bypassed.
11. Auth loss stops safely and creates user attention.
12. GitHub source/tests/build provenance matches the live binary tested.

A static unit test, successful compilation, or one lucky send is not sufficient.

## 23. Rollback/cutover

M1 is developed beside the current v1.3.6 relay.

Until acceptance passes:
- current runtime remains the live baseline
- new M1 relay is feature-gated
- existing foreground SendKeys relay is not promoted or expanded

At cutover:
1. verified M1 provider becomes default
2. unsafe current-focus SendKeys path is disabled by default
3. emergency fallback requires explicit user opt-in and clear warning
4. rollback to the last verified PCBridge build remains available

## 24. Definition of “automation possible”

After M1 the following user experience must be true:

> The user can start a substantial safe task, leave PCBridge running, use the PC for games/video/web normally, and PCBridge can repeatedly wake the correct ChatGPT conversation and continue the same run until completion or a genuine human/HARDLOCK/auth blocker occurs.

That, not the existence of a “Continue” button, is the milestone.
