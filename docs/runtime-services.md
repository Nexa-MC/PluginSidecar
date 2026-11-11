# Runtime services

`PluginHostRuntime` installs 77 implemented operations from the ABI catalog. The
embedding Host still supplies schema policy, verified acquisition, grants and
launcher business adapters. This is a runnable library kernel, not an installed
Desktop Sidecar. The CLI remains a scaffold. No production signing key or live
Market grant endpoint is provisioned by these changes.

## Publication and callbacks

Contracts, services, actions, predicates, workflows, event definitions and owned
state declarations stage during Initialize. Group commit publishes them together
with content. Aborting removes the group; stopping retires definitions, bindings,
subscriptions and callbacks. A required service binding adds a runtime dependency:
provider stop also seals/stops its required consumers, consumers first. An optional
binding becomes unavailable. Bindings never silently attach to a replacement load.

Only the coordinator dispatches handlers, with the admitted identity/context,
cancellation and causal metadata. Nested callback cycles are rejected. Host service
calls return an owned operation whose typed result is read through Tasks.Read.
Native-execution service methods and unapproved cross-service delegation are denied.
Actions validate input/output schemas and declared capability/scope requirements.
Actions requiring confirmation remain denied until a trusted UI confirmation port
is integrated. Non-read-only action idempotency keys bind inputs within a load;
a reused key with different arguments fails instead of invoking the handler again.

## State, events and subscriptions

An unread initialized state has Null content and the explicit `uninitialized`
revision. Both update methods check ownership, scope, schema and expected revision.
State/watch and operation/watch notifications are bounded. `Resources.ReadSubscription`
consumes up to 256 items, reports gaps and binds the returned batch to the call's
idempotency key; retries must reuse that key. `Resources.ReleaseSubscription`
retires either kind of subscription. Both methods check the admitted owner.

Event callbacks support transient and resumable subscriptions, best-effort and
at-least-once delivery, self filtering and bounded event-ID deduplication. Queue
serializes callbacks; Reject drops a new delivery while busy and reports a gap;
AllowBounded runs at most four callbacks per subscription. The Host caps all event
workers at 64 and retains queue/in-flight payloads within a shared 16 MiB budget.
Callbacks never block event publication.
Queue overflow or callback failure sets a gap flag; reading a callback subscription
returns that flag without stealing messages from its handler. Callback failures
retry at 250 ms intervals for at-least-once subscriptions until success or release;
handlers must tolerate duplicate effects. Only published
subscriptions receive notifications. Event origin is supplied by Host, not trusted
from plugin data. Watches cannot silently claim cursor replay support.

## Workflow execution and recovery

Node Inputs equal to Null use the previous result; other Inputs are literal data.
Every node validates its declared input/output schema. Sequence pipes results;
If evaluates the target predicate; Switch matches a string to a child ID (or
`default`); ForEach returns an array; Parallel obeys its concurrency bound and
All/AnySuccess completion policy. Return exits the current flow/parallel branch.
Action/Compensate invoke a declared action. Delay is cancellable. Retry uses only
its declared retryable codes, bounded attempts and backoff. Try runs its second
child on a business failure; Finally always attempts its second child with the
same cancellation token. Subflow invokes a declared workflow under the parent
work/depth budget. Checkpoint flushes the current persistent run journal.

Runtime limits are at most 32 nesting levels, 4096 node executions/trace records,
32 parallel branches, 16 retry attempts and five minutes per asynchronous run.
Workflow concurrency supports Parallel, Queue, RejectDuplicate and ReplacePending.
Queued cancellation preserves the predecessor fence. Replacement cancels queued
work only. WaitEvent matches the exact declared event/schema/scope; a transient
wait interrupted by reload requires resynchronization. AskUser requires the
trusted Host interaction port and an explicit UI permission. Missing UI fails.
Persistent Delay resumes its saved deadline instead of restarting the delay. Predicates may use a
handler, a Boolean literal, or the bounded `op` expression grammar: `input` with
`field`, `not` with one argument, `eq` with two, and `all`/`any` with `args`.

Persistent runs write versioned, bounded closed-binary journals using flush plus
atomic replacement. Journals are private to the plugin ID and plugin version.
Resume requires the current revision and unchanged full workflow definition/input;
completed outputs and early-return markers are reused. An effect is recorded
before dispatch and cleared only after a validated result. If that result is
unknown after interruption, the run becomes OUTCOME_UNKNOWN and cannot be replayed.
There is no invented successful recovery and no automatic retry after restart.
RetryStep only resumes a known failed step/run when these same checks allow it.
Read/ListRuns load the current owner's saved runs; Tasks.Dismiss removes a terminal
run and its journal. Journal corruption is an explicit error, not an empty history.
The store assumes one trusted Host owner, as does private plugin storage.

## Limits and acceptance

Registrations, state bytes, diagnostics, queue bytes, operation results and action
memoization have independent memory bounds. At most 256 operations are retained.
Persistent journals are limited to 256 files and 64 MiB. Ordinary stop revokes all
in-memory handles, while authorized saved workflows can be inspected after reload.

Tests load actual managed entries through the coordinator and execute callbacks,
service binding, state CAS/watch, event delivery, task ownership, action
idempotency, workflow branching/early return/cancellation, rollback and stop
closures. A separate test restarts the Host and resumes a checkpoint, then proves
that an uncertain effect is not executed again. These run under NativeAOT too.
C/Wasm conformance independently covers all 363 current/legacy SDK slots and 292
model/collection shapes; that count is not a claim of 363 launcher integrations.

On Windows, the Wasm adapter uses explicit trap checks (`WithSignalsBasedTraps(false)`).
This preserves .NET apphost CET shadow-stack protection while avoiding Wasmtime's
incompatible signal-based trap recovery ([upstream issue 374](https://github.com/bytecodealliance/wasmtime-dotnet/issues/374)).
CI exercises unreachable instructions, memory bounds, division by zero and fuel
exhaustion as well as compiled C decoder rejection, in managed and NativeAOT hosts.

## Schedules

The six schedule routes implement interval, absolute-time and event triggers,
explicit timezone validation, enable/disable, manual trigger and next-run queries.
The runtime owns a 250 ms pump; tests or embedding Hosts may supply a TimeProvider
and disable automatic polling. Skip, Coalesce and ReplayBounded govern missed
triggers; event debounce retains the latest matching payload. Schedule dispatch
re-enters workflow authority with the owner's grants. It cannot confer workflow
or event permissions. Stop and disable fence further dispatch.

Schedule state persists by plugin version and exact definition. The due time is
advanced and flushed before dispatch: a crash in this window may lose a trigger,
but cannot automatically replay an uncertain effect. Event debounce payloads are
transient and are not invented after restart. Limits are 256 schedules, 32 replayed
occurrences per poll, 1 second to 30 day intervals, and 5 minute debounce.

## Durable events and availability

Durable event publication flushes an atomic binary history before dispatch. Each
stream retains at most 256 events (or its smaller declared queue limit) and 1 MiB;
one event is limited to 256 KiB. There are at most 64 persisted streams. Resumable
subscription cursors identify persisted checkpoints bound to subscriber plugin ID,
stream and exact scope. Store the returned cursor for the next Initialize callback.
Subscribe remains an Initialize-only registration under the existing ABI.

Successful callback acknowledgement advances the checkpoint. Parallel completion
cannot advance it past an earlier outstanding delivery. Restart may repeat a
callback that completed before its acknowledgement was flushed. Retention loss,
queue overflow and rejected reentry report resynchronization explicitly. Up to
4096 checkpoints persist; explicit ReleaseSubscription deletes its checkpoint,
while plugin stop retains it. Starting a fresh subscription establishes a new
checkpoint after the author has resynchronized application state. Durable
WaitEvent nodes use this same history to recover events published while suspended;
expired history fails with RESYNC_REQUIRED instead of silently skipping events.

Contracts.WatchAvailability and Plugins.WatchAvailability queue typed
CapabilityDescription snapshots through Resources.ReadSubscription. They deliver
an initial snapshot, followed by changes at group publication and retirement.
Service snapshots honor provider identity and the requested version range; no
binding silently switches to a new provider. Staged declarations are not exposed.
