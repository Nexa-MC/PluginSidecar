# Installation ownership migration

The Sidecar now has a tested transaction for installing an approved developer
package. Production Worker composition has **not** switched to this path. The
Host still owns its existing picker, consent UI and restoration loop. Do not run
both owners against the same installation directory.

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

## Remaining production wiring

One management owner must serialize import, restoration, disable and Market
installation. It must observe and drain these asynchronous effects during
shutdown, recheck policy after picker/consent interactions, and retire active
loads when the session or developer policy is revoked. The installer checks an
individual attempt; it is not a background policy monitor.

That owner must also implement dependency-ordered offline restoration and
conditional cleanup after exact-load disable. `PluginApplication.StopLoadAsync`
returns all identities retired through dependency shutdown. Deleting a record by
plugin ID alone can erase a newer installation; use the observed catalog revision
for cleanup. Production restoration remains disabled in this new path until the
Worker owns the entire lifecycle and the Host owner is removed.

`CatalogRevisionTests` exercises competing catalog objects and a separate process
holding the directory lock. `ApprovedInstallationTests` exercises real Wasm
activation, rollback, policy revocation, cancellation, concurrent record changes
and queued byte snapshots. Both suites run in the managed and NativeAOT test
executables. These tests validate the transaction, not the unfinished production
picker, consent or startup integration.
