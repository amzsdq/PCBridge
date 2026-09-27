# Relay reliability

## Goal

Reliably deliver a continuation/handoff message to one exact ChatGPT conversation without disturbing the user's foreground activity and without creating duplicate agent runners.

“Exactly once” cannot be assumed across browser/network boundaries. PCBridge should implement at-least-once-capable delivery plus idempotent consumption and ambiguity fencing.

## Identity

Every handoff carries:
- `run_id`
- monotonic `seq`
- unique `message_id` / nonce
- target provider
- exact target conversation identity
- payload hash
- predecessor/expected baton identity when applicable

A repeated `message_id` must never start duplicate work.

## Durable outbox

Persist the intent before attempting delivery.

States:
`PENDING -> DISPATCHING -> DELIVERED`
or
`DISPATCHING -> AMBIGUOUS`
or
`DISPATCHING -> RETRY_WAIT`
or
`* -> BLOCKED_AUTH / TARGET_MISMATCH / FAILED_TERMINAL`

Do not mark success on click/composer mutation alone.

## Strong receipt

For ChatGPT browser delivery, success should require structural evidence such as:
- user-message count increased in the exact target conversation, or
- assistant generation began for that new turn, plus target identity still matches.

This follows the useful Parrot design. Semantic parsing of arbitrary chat prose is unnecessary for delivery acknowledgement.

## Ambiguity fence

If a submit action occurred but strong receipt was not observed:
1. persist AMBIGUOUS before notifying other components
2. fence automatic resend of that message_id
3. reconcile the target DOM/state
4. only retry after proving the original was not accepted, or after explicit operator resolution

This prevents duplicate CONTINUE messages and concurrent successor agents.

## Retry policy

Classify errors:
- transient network/server -> exponential backoff + jitter
- 429/rate limit -> honor retry hints where available, then bounded backoff
- generating/busy -> wait for structural idle
- target not loaded -> provider-specific recovery
- auth expired/challenge -> BLOCKED_AUTH
- target mismatch -> fail closed
- ambiguous receipt -> no blind retry

## Runner fencing

Only one effective runner may own a task generation at a time. Use a generation/fencing token; stale runners must fail `isCurrent` checks before side effects.

## Browser provider requirements

The target design is background-safe:
- dedicated browser context/profile or other provider-owned session
- exact conversation navigation/validation
- DOM locators rather than screen coordinates
- no dependency on user's active tab
- no foreground activation
- auth state stored outside Git and protected as credential material

Playwright-style role/label/test-id locators and auto-wait are preferred where feasible.

## Current baseline warning

The v1.3.6 immediate SendKeys path is unsafe for normal use because it targets the current focus. It must be removed from the primary path after the durable browser provider passes live verification.
