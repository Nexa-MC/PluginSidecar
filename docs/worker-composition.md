# Standalone Worker composition

The executable selects `StandalonePluginRuntime` after the authenticated Sidecar
handshake. This process owns the plugin session, including application admission,
business adapters, offline management and presentation. Deploy it with a Host
that exposes the pinned generic catalogs and has removed its embedded plugin
owner. Running two owners against one installation catalog is unsupported.

Source cutover and process tests do not establish signed release availability or
production Market delivery; see [deployment status](roadmap.md).

## Startup order

1. Authenticate and negotiate the general binary Sidecar transport. Register the
   action, management and drawing-input commands in that order; their
   session-local command IDs are 1, 2 and 3.
2. Discover Host context and wait for the native window, with a two-minute
   deadline linked to the activation lifetime. No guest loads before readiness.
3. Discover the business, effects, shell, theme, clipboard, picker, confirmation,
   notification and presentation catalogs. The exact shared package pin defines
   their independent wire fingerprints. Missing discovery fails activation.
   The current package pin is `0.1.0-alpha.22`, built from Host commit
   `68bfa038dbe8bacae4a3fc51e258ea5bd125ce06`.
4. Bind every effect and the required native HTTP client, then create one
   application at `plugins/storage`. Its business adapters own the HTTP client
   and dispose it during retirement.
5. Open the existing `plugins/storage.installed` catalog with the platform key
   protector. Create one management controller and one publisher, then start the
   supervised presentation loop.

The default HTTP handler discovers the generic Host HTTP catalog. Requests run
through `FoundationHost.CreateHttpClient(allowAutoRedirect: false)`, inheriting
the launcher network policy. It never creates a direct-network fallback. Tests
also inject an owned client to verify construction and disposal. Safe mode must
prevent activation of the plugin Sidecar at the Host session boundary.

Uploads are limited to 16 MiB. Known lengths stream in chunks; unknown lengths
use one bounded buffer at a time. Responses stream in chunks of at most 60 KiB,
so large downloads do not require a full response buffer or large DTO support.
Each exchange belongs to the authenticated Sidecar session. Request cancellation,
response disposal and Worker retirement release it; native retirement and idle
expiry cover a lost channel. A failed Send or Read is never automatically retried,
because the native effect may already have occurred. Synchronous response reads
are unsupported; consumers must use asynchronous reads.

## Admission and publication

The application interprets guest calls locally. The Host receives generic
binary requests and prepared NXML only. Management callbacks use opaque tokens
from the current projection; guest actions and raster input have distinct
commands. Unknown command IDs, retired tokens and callbacks before readiness
are rejected.

The loop reconciles developer-mode policy, attempts offline restoration once per
admitted session, publishes views, consumes a bounded navigation queue and
reports native visibility. Navigation rechecks the exact active load. A busy
management operation does not stop ordinary publication; policy revocation
cancels it through the management controller.

Publication failure ends the session. The sole frame reader monitors background
completion even when the Host sends no further traffic. An unexpectedly successful
loop exit is also a failure: a live session must not silently stop updating UI.
This runtime does not automatically replay uncertain guest effects.

## Retirement and partial failure

Retirement seals incoming commands and cancels admitted work. Disposal drains
activation and the publication loop, then disposes management, presentation,
the application, business effects, HTTP and raster transport, the installation catalog
and key protection. Cleanup continues after an individual disposal failure and
reports aggregate failure. Cancellation callbacks that throw cannot skip the
remaining cleanup. Concurrent disposal calls share one completion.

The same cleanup path handles partial activation. Failing readiness creates no
application or HTTP client. Failing the HTTP factory creates no published page
or guest load. Native session retirement removes Host-owned views and raster
resources; local disposal does not assume a disconnected Host will acknowledge
new cleanup requests.

## Evidence and limits

The frozen binary fixture in `StandaloneRuntimeTests` exercises 17 independently
discovered catalogs with the default native HTTP handler (16 with an injected
client), real Wasm import, two-stage consent, a confirmed guest
action, disable, stale callback rejection, readiness cancellation and failed
activation. It also checks that no Desktop, Avalonia or `Nexa.Services` assembly
is loaded. The same tests are included in `PluginSidecar.AotSmoke`.

The production-process fixture starts the actual executable, performs bootstrap
authentication, admits its registrations and exercises real Wasm import, a
confirmed action, disable, stale callback rejection and graceful shutdown through
the native binary session. CI runs it against both managed and NativeAOT workers.

Socket peer tests establish that background failure wakes the reader and that
retirement remains bounded. Native Host tests separately establish the display,
input and resource contracts. These checks do not establish deployed Market
service compatibility, real OS key-store availability or signed release availability.
