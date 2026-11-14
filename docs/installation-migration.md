# Installation ownership migration

The Sidecar has a tested transaction and management controller for developer
installation, disable and offline restoration. Production Worker composition has
**not** switched to this path. The Host still owns its existing picker, consent UI
and restoration loop. Do not run both owners against the same installation directory.

## What the installer accepts

`PluginApprovedInstaller` takes a `PluginApplication`, a `PluginOfflineCatalog`
and a function that reads the current `PluginInstallationPolicy`. The policy
contains a session ID, developer-mode flag, readiness flag and safe-mode flag.
Loading requires a nonempty matching session, developer mode, readiness and
safe mode disabled. These values come from trusted Sidecar composition, never
from the package or its guest.

`InstallAsync` receives package bytes, the approved ID and version, an immutable
grant set, the remember choice, the session captured for approval and a
cancellation token. The caller must already have displayed the package identity,
unsigned-package warning and requested permissions, and obtained explicit
approval. Passing `remember: true` requires separate consent to automatic
restoration. A valid ZIP or matching payload hashes do not establish publisher
trust or replace consent.

The method snapshots the supplied bytes before waiting for another installation.
The snapshot is cleared when the attempt finishes. Mutating the caller's array
while an attempt is queued cannot alter the package that the user approved.

## Transaction behavior

| Situation | Runtime result | Offline record |
| --- | --- | --- |
| Remembered installation succeeds | The new load is active | Approved bytes and grants are saved |
| Session-only installation succeeds | The new load is active | Any previous record remains unchanged |
| Loading fails and no record existed | The failed load remains inactive | The attempt's new record is removed |
| Loading fails over an existing record | The failed load remains inactive | Previous bytes and grants are restored |
| A cooperating writer replaces the record during failure | Normal load failure is returned | The newer record is preserved |
| Policy changes or cancellation arrives during activation | The exact new load is retired | The attempt is rolled back |
| The same plugin ID is already active | The existing load remains active | No write occurs |

An existing active plugin must be explicitly retired before replacement. The
installer does not silently stop it or its dependents. Session-only installation
also does not forget an older remembered version: disabling automatic restoration
is a separate management action.

Policy is checked before persistence, immediately before loading and after the
load finishes. A permission grant absent from the package's requested permissions
is rejected for both remembered and session-only installation. Runtime admission
still validates the package's ABI, dependencies, target and capabilities.

## Failure and cleanup

A completed runtime attempt returns `PluginLoadReport`; inspect its `State`, not
just whether the task completed. Invalid packages, stale sessions, cancellation
and catalog failures may instead throw. A failed report has already had its
remembered-record rollback attempted before it is returned.

Cleanup uses an uncancelled token so cancelling an import does not also cancel
restoration of the previous record. Waiting for another process's catalog file
lock is bounded to five seconds. If retirement or rollback fails, an `AggregateException` with
message `plugin.install.cleanup-failed` preserves both the initiating failure and
cleanup errors. The management owner must show failure and reconcile runtime and
catalog state; it must not display success or automatically retry the import.

The catalog's revision is a hash of the complete encrypted record. Each write
returns a new revision, even when package bytes are unchanged. Rollback replaces
or removes only the revision written by that installation attempt. Re-encrypting
the previous record restores its contents, not its old revision. The file format
remains `NEXAPC01`; revisions are not guest handles or publisher signatures.

## Management controller

`PluginManagementController` implements `IPluginManagementActions`. Construct it
before loading any plugin and give it the application, catalog, current-policy
reader, native picker and consent callbacks. The callbacks receive cancellation
tokens and must honor them. The controller borrows the application and catalog;
dispose it before disposing those dependencies.

`ImportDeveloperPackageAsync` reads at most 25 MiB from the chosen file and verifies
the complete package before requesting consent. The consent callback receives the
ID, version and requested permissions. It returns `Declined`, `SessionOnly` or
`Remember`. The controller rechecks the captured session after both interactions
and then uses the approved installer. A failed load is surfaced to the caller.

`DisableAsync` accepts an exact `PluginIdentity`. A stale identity is rejected
before any catalog write. The method snapshots record revisions, stops that load
and its required dependents, then removes matching records of loads actually
retired. A concurrent newer record is preserved and `plugin.disable.record-changed`
is reported. Cancellation after retirement starts does not cancel this cleanup.

`RestoreOfflineAsync` returns an array of `PluginRestoreIssue` values. It validates
records independently, retains only IDs, versions, revisions and dependencies
during planning, then reopens one package at a time. Required dependencies load
first. Invalid records, changed revisions, runtime failures and dependency cycles
are reported without deleting records; unrelated valid plugins still load.
Repeated calls in the same permitted session do not repeat the attempt.

`SynchronizePolicyAsync` cancels a pending picker or confirmation when its session
is no longer permitted, waits for that operation's cleanup, and retires active
loads. It preserves remembered records and resets restoration so a later permitted
session can restore them. `DisposeAsync` cancels and drains pending work, retires
loads, and rejects new calls. Concurrent disposal calls await the same cleanup.

## Remaining production wiring

The Worker must supply native picker and consent implementations, observe errors
from management commands, display restoration issues, and drive policy
synchronization when readiness, session or developer mode changes. `IsBusy` is
available for the management-page policy. All state-changing management calls are
serialized by the controller; the Worker must not load or stop plugins through a
second owner. Market admission must enter this same lifecycle rather than create
another restore or mutation loop.

Production restoration remains disabled in this new path until Worker composition
switches to the controller and the Host owner is removed. The installer checks an
individual attempt; the controller reacts when driven by the Worker. Neither
class starts a background policy monitor merely by being constructed.

`CatalogRevisionTests` exercises competing catalog objects and a separate process
holding the directory lock. `ApprovedInstallationTests` exercises real Wasm
activation, rollback, policy revocation, cancellation, concurrent record changes
and queued byte snapshots. Both suites run in the managed and NativeAOT test
executables. `ManagementControllerChecks` additionally exercises real Wasm import,
dependency restoration, exact disable, policy cancellation, corrupt records,
dependency cycles, concurrent record replacement and shutdown. These tests do not
claim that the unfinished production picker, consent or startup integration has
switched owners.
