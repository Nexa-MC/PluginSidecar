# Security

Do not put credentials, account data, signing keys or exploit details in public issues. Report privately through [GitHub private vulnerability reporting](https://github.com/Nexa-MC/PluginSidecar/security/advisories/new) when available, or contact a Nexa-MC maintainer privately. No response-time guarantee is currently offered.

The CoreCLR runtime adapter loads caller-verified plugin bytes. It does not verify package signatures or authorize plugins; integrations must complete those checks before calling it. The CLI does not expose plugin loading. A separate process is not an OS sandbox. Future admission must verify provenance and capabilities before code runs; plugins must never receive launcher credentials implicitly.
