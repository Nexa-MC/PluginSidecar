# Standalone Worker composition

`StandalonePluginRuntime` is the staged owner of a complete plugin session. The
production executable still selects `RemoteExecutionRuntime`; switching that
entry also requires removing the Desktop plugin owner, supplying the committed
network policy and connecting Market delivery. Do not run both application
owners against the same installation catalog.

## Startup order

1. Authenticate and negotiate the general binary Sidecar transport. Register the
   action, management and drawing-input commands in that order; their
   session-local command IDs are 1, 2 and 3.
2. Discover Host context and wait for the native window, with a two-minute
   deadline linked to the activation lifetime. No guest loads before readiness.
3. Discover the business, effects, shell, theme, clipboard, picker, confirmation,
   notification and presentation catalogs. The exact shared package pin defines
   their independent wire fingerprints. Missing discovery fails activation.
4. Bind every effect and the required native HTTP client, then create one
   application at `plugins/storage`. Its business adapters own the HTTP client
   and dispose it during retirement.
5. Open the existing `plugins/storage.installed` catalog with the platform key
   protector. Create one management controller and one publisher, then start the
   supervised presentation loop.

The HTTP factory is mandatory. Returning a default `HttpClient` in production
would bypass launcher proxy, PAC, DNS and provider settings. Tests inject an
owned client to verify construction and disposal; that is not a network-policy
implementation. Safe mode must prevent activation of the plugin Sidecar at the
Host session boundary.

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
the application, business effects, raster transport, the installation catalog
and key protection. Cleanup continues after an individual disposal failure and
reports aggregate failure. Cancellation callbacks that throw cannot skip the
remaining cleanup. Concurrent disposal calls share one completion.

The same cleanup path handles partial activation. Failing readiness creates no
application or HTTP client. Failing the HTTP factory creates no published page
or guest load. Native session retirement removes Host-owned views and raster
resources; local disposal does not assume a disconnected Host will acknowledge
new cleanup requests.

## Evidence and limits

The frozen binary fixture in `StandaloneRuntimeTests` exercises 16 independently
discovered catalogs, real Wasm import, two-stage consent, a confirmed guest
action, disable, stale callback rejection, readiness cancellation and failed
activation. It also checks that no Desktop, Avalonia or `Nexa.Services` assembly
is loaded. The same tests are included in `PluginSidecar.AotSmoke`.

Socket peer tests establish that background failure wakes the reader and that
retirement remains bounded. Native Host tests separately establish the display,
input and resource contracts. These checks do not establish deployed Market
service compatibility, real OS key-store availability or production cutover.
