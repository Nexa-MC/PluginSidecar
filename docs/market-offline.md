# Market delivery and offline use

English | 简体中文 summary below

`Nexa.Plugin.MarketDelivery` is a development component. `IMarketAcquisition` is the online Market port; the current production Market has no implementation of this port. Do not advertise the component as a working market installation route.

## Responsibilities

Market retains catalog/search, purchases, ownership, account eligibility, publisher verification and current artifact security decisions. After those checks, the trusted issuer produces an encrypted payload and signed grant. The publisher signing private key belongs on the trusted service, never in Market Sidecar or PluginSidecar. Existing Ed25519 purchase receipts are not this new RSA package grant and must not be treated as interchangeable.

Host supplies a pinned trusted publisher public key, the protected device private key and selected local subject. Persist the device key in the OS credential facility; do not persist raw private keys beside packages. The plugin process receives only verified plaintext, not login credentials or content keys. Host still grants the final load permit and checks manifest/entry compatibility.

`OfflinePluginStore.AcquireAsync` downloads through the Market port, verifies and decrypts in memory, clears verification plaintext, then atomically replaces one encrypted cache file. A failed download, invalid replacement or canceled write preserves the old cache. `OpenOffline` performs no HTTP and requires no login session. A new store/verifier can reopen the cache using the same protected device identity.

## Cryptographic profile (development v1)

- AES-256-GCM payload encryption, random 12-byte nonce, 16-byte tag.
- Content key wrapped for the device using RSA-OAEP-SHA256.
- Grant signed with RSA-PSS-SHA256, binding plugin ID/version, subject, device SPKI SHA-256, plaintext and ciphertext SHA-256, payload size, nonce, tag, wrapped key and validity interval.
- Trusted keys are supplied out of band by Host; no package-supplied key becomes trusted.
- Maximum plaintext/ciphertext size 25 MiB, grant 8192 bytes, signature 1024 bytes; reject malformed lengths before allocation.
- Grant v1 uses little-endian signed 32-bit version, length-prefixed strict UTF-8 identity strings, signed 64-bit Unix validity seconds (`Int64.MaxValue` for no end), three 32-byte digests, signed 32-bit payload size, nonce, tag, signed 32-bit wrapped-key length and key. Signature covers exactly these bytes, including all lengths.
- Cache v1: four little-endian Int32 fields (version, grant length, signature length, ciphertext length), followed by those three blobs; exact file length required. This is a local package envelope, **not a new Host IPC format**.

## Offline policy

Permanent ownership has no mandatory online-refresh TTL. Time-limited grants are checked at each load; expiry does not stop an already-running plugin. Revocation learned online may block future acquisition; an indefinitely offline machine cannot learn new revocations. Local wall-clock checks cannot prevent an administrator rolling the clock back. No claim of DRM-resistant memory secrecy or an OS sandbox is made.

Offline operation requires prior successful acquisition, an intact cache, a trusted publisher key, the same protected device key and the authorized locally selected subject. Moving only the cache to another device does not grant access. Account bearer tokens and login-session expiry do not participate in `OpenOffline`.

## Integration still required

1. Backend: a reviewed plugin delivery endpoint, a provisioned package-signing key, ownership/security checks and immutable plugin manifest/package format. Current `/store/items/{id}/artifact` rejects plugins; do not silently repurpose theme delivery.
2. Market Sidecar: authenticated online acquisition adapter and explicit install/update UI. Never send bearer tokens or signer keys across Host IPC.
3. Host: existing binary ABI capability routing, protected device-key custody, offline subject selection, load permits and cache ownership/quota policy.
4. Loader: verified package manifest/dependency parsing before `MemoryPlugin.LoadVerifiedPayloads`, followed by six-stage group loading.

No production service, publisher key or launcher route is deployed by this component.

## 简体中文

Market 负责市场、拥有权、在线加密交付和签名授权；Host 保管可信公钥、设备私钥与当前本地账户身份；PluginSidecar 在每次加载时独立验证签名、授权和解密摘要。离线缓存仅包含密文、签名票据与设备公钥包裹的内容密钥，不保存登录令牌或明文内容密钥。

永久授权不强制联网续期，限期授权在加载时检查，运行中不因到期定时停用。离线可用的前提是已成功获取过插件且本机保护密钥仍可用；完全离线时无法获知新的服务端撤销。当前已实现并测试密码学和缓存模块，但生产市场接口、Market UI 与 Host 二进制路由仍待接入，不能宣称实际插件安装已打通。
