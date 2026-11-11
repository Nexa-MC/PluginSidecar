#!/usr/bin/env python3
"""Build an import-free C guest and deterministic developer ZIP (not a Market grant)."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import zipfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--clang', default='clang')
parser.add_argument('--out', type=Path, default=root / 'artifacts/wasm-sample')
args = parser.parse_args()
args.out.mkdir(parents=True, exist_ok=True)
wasm = args.out / 'plugin.wasm'
subprocess.run([args.clang, '--target=wasm32-unknown-unknown', '-std=c11', '-O2',
                '-Wall', '-Wextra', '-Werror', '-ffreestanding', '-fno-builtin', '-nostdlib',
                '-I', str(root / 'sdk/wasm/include'), str(root / 'samples/WasmHello/plugin.c'),
                '-Wl,--no-entry', '-Wl,--export-memory', '-Wl,--initial-memory=262144',
                '-Wl,--max-memory=524288', '-o', str(wasm)], check=True)
payload = wasm.read_bytes()
manifest = {
    'manifestVersion': 1, 'pluginId': 'example.wasm', 'pluginVersion': '1.0.0',
    'bootstrapVersion': 1, 'entry': {'assembly': '', 'type': ''},
    'target': {'operatingSystems': [], 'architectures': [], 'runtime': 'nexa-wasm-1'},
    'declaredAbiCandidates': [100], 'requiredDependencies': [], 'optionalDependencies': [],
    'conflicts': [], 'requestedPermissions': ['nexa.definitions.write'],
    'requiredCapabilities': [{'contractId': 'nexa.definitions', 'minimumVersion': 1}],
    'optionalCapabilities': [], 'webAssemblyEntryPath': 'plugin.wasm',
    'payloads': [{'path': 'plugin.wasm', 'size': len(payload), 'sha256': hashlib.sha256(payload).hexdigest()}]
}
with zipfile.ZipFile(args.out / 'plugin.zip', 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    for name, data in [('plugin.json', json.dumps(manifest, sort_keys=True, separators=(',', ':')).encode()), ('plugin.wasm', payload)]:
        entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
        entry.external_attr = 0o100644 << 16
        archive.writestr(entry, data)
print(args.out / 'plugin.zip')

# Compile every generated SDK call/model, plus the callback lifecycle fixture.
for source, output in [('tests/wasm/conformance.c', 'conformance.wasm'), ('tests/wasm/callback.c', 'callback.wasm'), ('tests/wasm/codec.c', 'codec.wasm')]:
    subprocess.run([args.clang, '--target=wasm32-unknown-unknown', '-std=c11', '-O1', '-Wall', '-Wextra', '-Werror',
                    '-ffreestanding', '-fno-builtin', '-nostdlib', '-I', str(root / 'sdk/wasm/include'), str(root / source),
                    '-Wl,--no-entry', '-Wl,--export-memory', '-Wl,-z,stack-size=131072',
                    '-Wl,--initial-memory=1048576', '-Wl,--max-memory=1048576', '-o', str(args.out / output)], check=True)
