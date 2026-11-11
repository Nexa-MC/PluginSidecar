#!/usr/bin/env python3
"""Bundle the current NuGet packages, C SDK and documentation without stale artifacts."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parents[1]
packages = root / 'artifacts/packages'
names = ['Nexa.Plugin.Bootstrap.V1', 'Nexa.Plugin.Sdk.Abi100', 'Nexa.Plugin.Hosting',
         'Nexa.Plugin.MarketDelivery', 'PluginSidecar.WasmRuntime', 'PluginSidecar.Packages', 'PluginSidecar.RuntimeAdapter']
files = {}
for name in names:
    version = ET.parse(root / f'src/{name}/{name}.csproj').findtext('.//Version')
    package = packages / f'{name}.{version}.nupkg'
    files['packages/' + package.name] = package.read_bytes()
version = ET.parse(root / 'src/Nexa.Plugin.Sdk.Abi100/Nexa.Plugin.Sdk.Abi100.csproj').findtext('.//Version')
c_sdk = packages / f'Nexa.Plugin.WasmSdk.{version}.zip'
files[c_sdk.name] = c_sdk.read_bytes()
for directory in ['docs', 'contracts']:
    for path in sorted((root / directory).rglob('*')):
        if path.is_file():
            files[path.relative_to(root).as_posix()] = path.read_bytes()
for name in ['LICENSE', 'SECURITY.md', 'CONTRIBUTING.md', 'CONTRIBUTING.zh-CN.md']:
    files[name] = (root / name).read_bytes()
files['README.md'] = f'''# Nexa plugin SDK {version}

This development bundle contains seven NuGet packages, the freestanding C/Wasm
SDK and the current loading/runtime documentation. Add `packages/` as a local
NuGet source alongside nuget.org for third-party dependencies. Extract
`Nexa.Plugin.WasmSdk.{version}.zip` for headers and the runnable C examples.

- [Loading contract](docs/loading-contract.md)
- [Runtime services and limits](docs/runtime-services.md)
- [C/Wasm authoring](docs/wasm-sdk.md)
- [Implemented and remaining acceptance](docs/roadmap.md)
- [Source, tests and build instructions](https://github.com/Nexa-MC/PluginSidecar)

The SDK exposes 363 current/legacy calls; the local kernel implements 90 routes.
Desktop business/UI integration, production Market admission and a signed `.nsc`
are separate work. This bundle is not an installed launcher update or a NuGet.org
publication. `SHA256.json` checks file integrity, not publisher authenticity.

## 简体中文

这是 SDK 开发包，包含 7 个 NuGet 包、C/Wasm SDK、示例和契约文档。
将 `packages/` 加入本地 NuGet 源，并保留 nuget.org 以还原第三方依赖。
C SDK 压缩包包含头文件，以及实际运行过的基础示例和工作流示例。

SDK 提供 363 个当前及历史调用绑定，本地运行库实现 90 项操作。
Desktop 业务/UI 接入、生产 Market 准入和签名 `.nsc` 尚未完成。
本包不是启动器更新，也未发布到 NuGet.org。校验清单用于检查文件完整性，不能代替发行签名。
'''.encode()
files['SHA256.json'] = (json.dumps({name: hashlib.sha256(data).hexdigest() for name, data in sorted(files.items())}, sort_keys=True, indent=2) + '\n').encode()
output = packages / f'Nexa.Plugin.Sdk.Bundle.{version}.zip'
with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    for name, data in sorted(files.items()):
        info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
        info.external_attr = 0o100644 << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(info, data)
print(output)
