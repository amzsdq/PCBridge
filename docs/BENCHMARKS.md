# Benchmarks and reference patterns

This document records what PCBridge should learn from existing systems. References are architectural evidence, not authority to copy blindly.

## Parrot (amzsdq/parrot)

Useful, partial adoption.

Strong ideas to port/reuse:
- exact-target routing
- DOM-based ChatGPT adapter
- strong send receipt from structural evidence
- durable ambiguous state before notification
- automatic-resend fence after ambiguous delivery
- queue dedupe
- token-fenced single effective runner
- structural cooldown/backpressure
- dashboard attention states

Do not adopt as the whole runtime:
- current architecture depends on an open Chrome tab/extension
- authenticated real-ChatGPT M6 validation is still incomplete
- PCBridge requires Chrome-off/background operation and OS-level policy/recovery

Reference: https://github.com/amzsdq/parrot

## Temporal

Temporal treats long-running work as durable workflow state/history, survives worker crashes, uses messages/signals for external control, and supports human approval patterns. Its current AI guidance also models tool/model calls as durable Activities with retries/timeouts.

Adopt concepts:
- durable event history/state transitions
- clear workflow/activity boundary
- external signals for human control
- fencing of in-flight work
- continue-as-new / history compaction for very long sessions
- explicit retry policy rather than ad-hoc loops

References:
- https://docs.temporal.io/ai
- https://docs.temporal.io/develop/python/integrations/strands-agents

Decision: benchmark architecture; do not add Temporal as a PCBridge runtime dependency yet.

## Microsoft Durable Functions / Durable Task

Human interaction is modeled as a durable wait for an external event, often raced against a durable timer.

Adopt:
- supervisor input as external event
- approval timeout/escalation as explicit workflow state
- no compute-burning busy wait while awaiting a human

References:
- https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-human-interaction
- https://learn.microsoft.com/azure/durable-task/common/durable-task-external-events

## LangGraph

LangGraph makes persistence/checkpointing and `interrupt()`/resume first-class. Its guidance separates transient, model-recoverable, user-fixable and unexpected errors.

Adopt:
- checkpoint at atomic boundaries
- user steering as resumable state, not a conversational accident
- error taxonomy with different recovery paths
- deterministic/reliable steps separated from agentic decisions

References:
- https://docs.langchain.com/oss/python/langgraph/overview
- https://docs.langchain.com/oss/javascript/langgraph/thinking-in-langgraph

## Transactional outbox / idempotent consumer

AWS guidance: persist state change + outbound event intent together; consumers must tolerate duplicates; preserve ordering.

Adopt for relay:
- durable outbox before send
- sequence numbers
- idempotent receiver
- no notification if local state transaction rolled back

Reference:
- https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html

## Stripe-style idempotency keys

Stripe safely retries mutating requests by associating a client-generated idempotency key with the first execution result and rejecting incompatible parameter reuse.

Adopt:
- stable message/action idempotency key
- bind key to payload hash/target identity
- duplicate request returns prior terminal result instead of re-executing

Reference:
- https://docs.stripe.com/api/idempotent_requests

## Runner ownership, leases and fencing

Parrot's token-fenced runner idea matches a broader distributed-systems concern: a stale worker can wake up after a newer owner has taken over.

Relevant references:
- Kubernetes Lease / leader-election concepts: https://kubernetes.io/docs/concepts/architecture/leases/
- etcd election API: https://etcd.io/docs/v3.5/dev-guide/api_concurrency_reference_v3/
- Martin Kleppmann on fencing tokens: https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html

Adopt:
- one active owner per task generation
- monotonically increasing generation/fencing token, not only a boolean lock
- side-effect sinks compare/reject stale generations where feasible
- lease/heartbeat primarily provides liveness; fencing protects correctness against delayed stale workers

Decision: PCBridge is local-first, so this does not require a distributed consensus dependency. The same correctness principle should be implemented in the durable local task store.

## Retry / backoff

Transient and throttling failures should use bounded exponential backoff; non-transient failures should fail fast. Retries are safe only when the operation is idempotent or otherwise fenced.

Reference:
- https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/retry-backoff.html

## Playwright

Relevant browser reliability patterns:
- semantic locators by role/label/test-id
- auto-wait/retry instead of fixed sleeps
- browser contexts for isolation
- reusable authentication state
- authentication state is credential material and must never enter Git

References:
- https://playwright.dev/docs/locators
- https://playwright.dev/docs/auth
- https://playwright.dev/docs/api/class-browsertype

Decision: evaluate as the first background-browser implementation candidate, but require real authenticated ChatGPT smoke tests before selecting it permanently.

## OpenTelemetry

Trace context gives causal linkage across process/tool boundaries.

Adopt:
- stable trace/run identity
- spans for atomic work/tool calls/relay attempts
- structured events for approvals, checkpoints, retries and human steering

References:
- https://opentelemetry.io/docs/specs/otel/overview/
- https://opentelemetry.io/docs/specs/otel/context/

## Product-UX references

n8n, Langflow, Dify, Flowise and agent-control-plane projects remain useful UI/operations benchmarks for execution history, graph/workflow visualization, operator attention and approval surfaces. Their code should only be imported after license and subsystem-fit review.

## Overall decision

Do not rebuild known distributed-systems problems from intuition. PCBridge should synthesize proven patterns, while keeping the runtime local, lightweight and specifically optimized for user-owned-PC control.
