# M1 research review — Chat On Steroids, Parrot, and durable automation patterns

Date: 2026-10-02
Status: REVIEW COMPLETE FOR M1 SPECIFICATION
Runtime implementation: NOT STARTED

## 1. Scope

This review answers one question:

> What should PCBridge reuse, adapt, or reject from Chat On Steroids (CoS), Parrot, and established durable-workflow/browser-automation patterns before implementing M1 Autonomous Operation Ready?

The priority is final PCBridge quality, not accepting any prior proposal or maximizing code reuse.

## 2. Sources pinned for this review

### Chat On Steroids
- Repository: https://github.com/totec448-spec/chat-on-steroids
- License: MIT
- Latest release reviewed: v2.1.25 — “Chats keep going on their own”
- Main commit reviewed: `a1879601684712cbc3d6100ee4fe2dacbdf1b7b4`
- Important files reviewed:
  - `AGENTS.md`
  - `src/main/session/input.ts`
  - `src/main/session/continuation.ts`
  - `src/main/session/correlation.ts`
  - `src/main/session/finish.ts`
  - `src/main/session/store.ts`
  - `src/main/goal.ts`
  - `src/main/bridge.ts`
  - `src/main/browser-startup.ts`
  - `src/main/browser-control.ts`
  - `src/main/control-api.ts`
  - `src/main/control-actions.ts`
  - `src/main/durable.ts`
  - `extension/background.js`
  - `extension/content.js`
  - `extension/browser-control.js`
  - `extension/chatgpt-dom.js`
- Incident/fix references:
  - #744 / PR #752 — composer remount before Send
  - #746 — durable continuation / lifecycle / ownership analysis
  - PR #755 — stale earlier final closing a newly started turn
  - roadmap #482 — larger continuation coordinator work

### Parrot
- Repository: https://github.com/amzsdq/parrot
- Main commit reviewed: `49aaad1ce9b603bcedef24d4b75420d8330b9e02`
- Candidate status: v0.8.7 NON-RELEASE pending authenticated real-ChatGPT validation
- Important files reviewed:
  - `extension/route-state.js`
  - `extension/runner-registry.js`
  - `extension/chatgpt-adapter.js`
  - `extension/background.js`
  - `docs/MILESTONES.md`

### External architecture references
- Temporal durable execution and workflow signals
- Microsoft Durable Task / Durable Functions external events and human interaction
- AWS transactional outbox
- AWS retry with exponential backoff
- Playwright locators, auto-wait and authenticated browser contexts
- Chrome Manifest V3 service-worker lifecycle and `storage.session`

These are reference patterns, not dependencies selected by this document.

## 3. Main conclusion

CoS is now a strong production-oriented reference for the exact class of failures PCBridge M1 must solve. It has repeatedly encountered and repaired real ChatGPT browser races that a fresh implementation would otherwise rediscover.

However, importing CoS wholesale would lower PCBridge quality because it would duplicate:
- local file/process tooling already supplied by Desktop Commander,
- the Electron shell,
- MCP surfaces,
- agent/workspace UI,
- Goal/Loop provider logic,
- worker orchestration,
- browser control unrelated to exact self-handoff.

The recommended strategy is:

> Keep PCBridge Core authoritative. Selectively port the small browser/session reliability mechanisms that have high empirical value, preserve upstream provenance, and rewrite the integration boundary around PCBridge’s stricter permission and non-interference model.

Parrot remains useful as a second independent reference for strong receipt, ambiguity fencing, queue dedupe and runner fencing, but it is not sufficiently live-validated to be the primary browser runtime.

## 4. What CoS gets right and PCBridge should adopt

### 4.1 One durable fact, one owner
CoS explicitly treats duplicated authority as a defect. Session identity, input state, browser command custody and continuation ownership each have one authoritative owner.

PCBridge should adopt this directly.

Examples:
- one owner for `run_id / generation`
- one owner for target conversation identity
- one owner for outbound handoff state
- one owner for browser delivery receipt
- one owner for HARDLOCK/blocking state

UI, browser companion and MCP tools may project those facts but must not independently decide them.

### 4.2 Exact identity plus epoch, not “current tab”
CoS tracks conversation, document/navigation identity and request/turn ownership rather than relying on selected tabs.

PCBridge should require:
- provider
- exact conversation id
- browser-profile id
- document/navigation epoch
- handoff message id
- run generation/fencing token

A title, selected tab, foreground window or URL prefix is never enough to mutate/send.

### 4.3 Durable outbox before browser side effects
CoS `session/input.ts` stores an authored input before browser delivery and separates queued/claimed/sent/uncertain states.

This matches the transactional-outbox pattern and should be central to M1.

For PCBridge, task state and outbound handoff intent must be committed in the same durable M1 state record before any browser Send.

### 4.4 Ambiguous send is a distinct terminal-for-auto-retry state
Both CoS and Parrot correctly reject the naive rule “if confirmation is missing, send again.”

If native Send may have happened but the receipt is missing:
- persist AMBIGUOUS
- do not auto-resend
- reconcile the target
- only retry after proving non-delivery

This is mandatory for M1.

### 4.5 Composer lease and pre-Send rebind
PR #752 fixes a real ChatGPT race:
- text is inserted,
- React replaces the composer,
- the original editor lease is stale,
- blindly clicking the new Send could duplicate or mutate the wrong draft.

Useful rule to adapt:
- before Send authorization only,
- same target,
- same exact draft hash,
- no attachments,
- no user edit,
- one safe rebind is allowed.

After Send authorization/dispatched, a lost composer becomes ambiguous; no automatic second Send.

### 4.6 Causal response ownership
PR #755 and #746 show that seeing an old final/terminal element after a new send can falsely close the new turn.

M1 therefore needs a response lease:

`handoff -> exact user message -> exact response turn -> terminal evidence`

A terminal observation only ends the turn when it is causally owned by the response to the exact handoff message.

### 4.7 Browser observation is evidence, not authority
CoS separates:
- page/browser observations,
- main-process durable ownership,
- tool truth.

PCBridge should use the same principle. A DOM state can suggest “sent”, “running” or “finished”, but the durable coordinator decides whether that evidence belongs to the current run/message/document epoch.

### 4.8 Durable barriers before acknowledgements
CoS uses explicit immediate durable writes for control transitions that must survive restart before an acknowledgement is emitted.

PCBridge already has an atomic file primitive using write + flush + replace. M1 should use a single authoritative state document for the first implementation so task transition + outbound intent cannot diverge across two files.

### 4.9 Local control API security pattern
CoS’s optional loopback control API contains several useful patterns for later PCBridge control surfaces:
- loopback only
- random per-launch token stored locally
- token never returned over HTTP
- reject browser Origin
- validate Host
- read-only default
- a separate switch for actions
- rate limit and body limits
- action routes reuse the existing durable outbox rather than creating another send path

This is useful for future mobile/local supervisor work, but it is not required to finish M1.

## 5. What Parrot adds

Parrot independently reinforces four rules:

1. **Strong receipt** — click/composer mutation is not delivery. It requires structural evidence such as user-message count increase or assistant generation.
2. **Ambiguity fence** — unconfirmed sends are durable `ambiguous`; automatic resend is disabled.
3. **Queue dedupe** — route identity and signal identity prevent repeated delivery.
4. **Runner token fencing** — stale runners cannot continue after a newer runner owns the route.

These are adopted as correctness requirements.

Parrot’s current extension architecture is not selected as the M1 runtime because its authenticated ChatGPT M6 gate remains incomplete and it assumes an existing browser-tab model.

## 6. What not to import from CoS into M1

### 6.1 Full Electron application
Reject. PCBridge already has a native Windows lifecycle/UI and a single-EXE goal.

### 6.2 CoS local file/terminal/MCP implementation
Reject for M1. Desktop Commander already provides the execution backend and replacing it would expand scope without improving the first milestone.

### 6.3 Goal’s second-model/OpenRouter decision engine
Reject for M1.

Reasons:
- extra provider/API dependency
- possible extra cost/credentials
- another correctness boundary
- M1 can use an explicit durable `handoff` / `complete` tool contract

A future autonomous-planning mode may add a decision model behind an adapter, but it must not be required for core self-handoff.

### 6.4 Worker/swarm orchestration
Defer to later milestones. M1 proves one durable run first.

### 6.5 Full Compact & Resume
Defer. M1 remains in one exact ChatGPT conversation. Context migration/new-chat compaction is a later feature after same-chat ownership is proven.

### 6.6 Existing-user-Chrome as the primary automation surface
Reject.

CoS intentionally integrates the user’s normal browser. PCBridge has a stricter requirement: Netflix, games, typing and ordinary browsing must not be disturbed.

M1 therefore uses a PCBridge-owned browser identity/profile. The user’s normal Chrome may be completely closed or actively used without becoming the automation target.

### 6.7 Foreground SendKeys / screen-coordinate automation
Reject as a normal path. Keep only as an explicit emergency diagnostic fallback, disabled by default.

## 7. Browser-runtime decision

### Primary design
Use a dedicated PCBridge browser profile with a minimal browser companion derived from the proven CoS/Parrot DOM and lifecycle techniques.

Properties:
- separate `--user-data-dir`
- separate provider profile id
- user signs into ChatGPT once in that dedicated profile
- PCBridge can launch it when the normal browser is closed
- no selected-tab or foreground dependency
- exact conversation navigation
- extension/content script observes actual ChatGPT DOM
- app/core owns durable state; the extension does not

Why this beats directly controlling the user’s Chrome:
- no wrong-chat coupling to current activity
- no focus/keyboard/mouse collision
- independent restart/recovery
- browser profile becomes a provider adapter boundary
- later Claude/Gemini adapters can use separate provider profiles

### Playwright
Keep as a reference/prototype/fallback candidate, not the selected primary M1 runtime yet.

Its semantic locators, auto-wait and reusable authenticated contexts are useful, but M1 should not assume headless browser behavior is equivalent to authenticated interactive ChatGPT. A real-provider bakeoff may later replace the extension provider if Playwright proves equally reliable.

## 8. Manifest V3 durability rule

Chrome extension service workers are intentionally ephemeral. Chrome may stop an idle service worker; `storage.session` also ends with the browser session.

Therefore:
- the extension is never the only durable owner of run/outbox state
- PCBridge Core persists every semantically important transition
- extension storage may retain browser-lifetime custody/journal data only
- browser restart must reconcile from PCBridge state, not replay extension memory blindly

## 9. Retry and backpressure rule

Retry is only for failures classified as transient and only when the action is idempotent or fenced.

Examples:
- 429 / throttling -> provider hint if available, otherwise bounded exponential backoff + jitter
- temporary network -> bounded backoff
- loading failure before Send -> reload/reopen exact target
- auth/challenge -> stop and create Human Attention
- target mismatch -> fail closed
- Send may have happened -> AMBIGUOUS, no blind retry

Do not loop every second on a blocked or ambiguous state.

## 10. Human steering interaction with automation

A queued human message outranks an automatic continuation that has not been dispatched.

Rule:
- before `SEND_AUTHORIZED`, newly received human GUIDANCE/COMMAND can cancel/supersede the pending auto baton and cause a re-plan
- after `SEND_DISPATCHED`, do not synthesize a second competing message; reconcile the first send and queue the human message behind it
- INTERRUPT cancels future automatic work and fences the current run at the earliest safe point

This follows durable-workflow external-event semantics rather than treating human steering as unrelated chat text.

## 11. Licensing/provenance decision

CoS is MIT licensed, so selective code reuse is allowed subject to license/attribution preservation.

If PCBridge directly ports code:
- pin upstream repository + exact commit
- record source file/function lineage
- preserve required MIT copyright/license notice
- place upstream notice under `third_party/`
- keep modifications reviewable rather than copying the entire application
- do not import unrelated third-party bundled assets merely because they exist in CoS

Parrot is owned in the same GitHub account but currently has no repository license. For clean provenance, treat it as an internal design reference unless/until its ownership/license is explicitly normalized.

## 12. Final adoption matrix

| Area | CoS | Parrot | PCBridge decision |
| --- | --- | --- | --- |
| Exact conversation identity | Strong | Strong | Adopt |
| Durable outbound state | Strong | Partial | Adopt; Core owns it |
| Strong receipt | Strong | Strong | Adopt |
| Ambiguous send fence | Strong | Strong | Adopt |
| Composer remount repair | Proven fix | Simpler | Adapt CoS |
| Response-turn ownership | Proven fixes | Limited | Adapt CoS |
| Retry/cooldown | Strong | Strong | Adapt, provider-specific |
| Browser extension DOM tracking | Strong, live project | Candidate | Select CoS as primary reference |
| Dedicated background browser | Not its primary product model | No | Build PCBridge-specific |
| Normal user Chrome independence | Insufficient for our invariant | Insufficient | Build PCBridge-specific |
| Local control API | Strong | No | Defer/adapt later |
| Goal decision model | Strong feature | No | Reject for M1 |
| Multi-agent workers | Strong feature | Fleet routing | Defer |
| File/terminal backend | Duplicates DC | No | Reject |
| Permission/HARDLOCK model | Different product | No | Keep PCBridge |
| Checkpoint/recovery policy | Different product | No | Keep PCBridge |

## 13. Research conclusion

The best M1 is not a fork of CoS and not a rewrite from scratch.

It is a synthesis:
- PCBridge Core: policy, task ownership, recovery, audit, durable run state
- Desktop Commander: local execution primitives
- CoS-derived browser/session reliability: DOM evidence, composer leasing, response ownership, browser journal ideas
- Parrot-derived relay invariants: strong receipt, ambiguity fence, dedupe, runner fencing
- durable-workflow patterns: external events, transactional outbox, idempotency, bounded retry
- PCBridge-specific browser isolation: dedicated profile with zero normal-user-session interference

Implementation should begin only from the corresponding M1 specification.
