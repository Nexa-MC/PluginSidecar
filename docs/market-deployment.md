# Market plugin delivery: deployment and recovery

The code path is implemented in three independent components. PluginCenter-Server
authorizes an exact approved plugin release and issues a device-bound encrypted
entitlement. Market Sidecar uses the current NexaCL account to acquire it.
PluginSidecar verifies it, asks the user to approve the declared permissions, and
loads the Wasm plugin. The launcher requires none of these components to start.

## Before enabling delivery

1. Apply PluginCenter-Server migration `0043_plugin_delivery.sql` to the intended
   database and deploy its plugin artifact validation and acquisition handlers.
2. Provision a dedicated RSA signing private JWK as the server secret
   `STORE_PLUGIN_DELIVERY_SIGNING_JWK`. Do not reuse the receipt signing key or
   commit the private JWK. Keep key generation and secret transfer in the trusted
   deployment environment.
3. Export the corresponding **public** SubjectPublicKeyInfo DER as canonical
   base64. Review it, then add one line to
   `src/PluginSidecar/market-publishers.txt`. Up to four reviewed RSA keys of
   2048–8192 bits are accepted. Keep old public pins while their perpetual offline
   entitlements are supported. Never fetch trust pins from the acquisition reply.
4. Build and verify the independent Market and Plugin Sidecar images. Publish
   their immutable six-RID manifests and update the launcher's reviewed locks.
   Package presence is optional; missing plugins must not become a startup error.
5. Enable `STORE_PLUGIN_DELIVERY_ENABLED=true` only after the deployed publisher
   key and distributed public pin match. Publish a validated, approved test plugin
   release, acquire ownership, and install it from the Market page.
6. Exit both Sidecars, disconnect the network, restart the launcher and
   PluginSidecar, and verify that the approved plugin restores in ordinary mode.
   Also test declining consent, logout during installation, missing PluginSidecar,
   a locked OS key store and an expired or tampered entitlement.

The repository intentionally contains no production publisher pin until the
server key is provisioned and reviewed. An empty set returns service unavailable;
it does not weaken signature verification or substitute a test signing key.
Local fixtures use temporary RSA keys and real Wasm packages, and do not establish
that production secrets, database state or signed distribution inputs are ready.

## State ownership and recovery

PluginSidecar stores the protected device identity in `plugins/storage.device`,
Market entitlements in `plugins/storage.market`, and developer installations in
`plugins/storage.installed`. Paths are relative to the launcher's data root.
Each Market record retains its original grant, signature, ciphertext and locally
approved permission set. Reopening verifies the publisher, device, account subject,
validity interval and package hashes before loading code. Offline restoration does
not need a Market bearer token. Moving the records without the OS-protected device
identity does not transfer their entitlement to another device.

The native query `sidecar.service.plugins.market` accepts only a Host-authenticated
Market caller. Its NPM1 payload carries Begin, Append, Finish, Status and Abort,
with 48 KiB chunks and sequential offsets. Finish admits one installation and
returns immediately. Market polls while native consent is pending. Two minutes
without activity retires the transfer; the client caps acquisition at 90 seconds
and consent at ten minutes. Logout and shutdown cancel admitted work. Abort is
not an uninstall operation and cannot reverse an effect already committed. After
a lost reply, inspect plugin management before retrying an installation.

Disabling an installed plugin removes its matching approved record and retires
its active load. Failed updates restore the previous record only when it is still
the observed revision. Corrupt records and inaccessible device identities fail
closed; support tools must not silently delete or rotate them.
