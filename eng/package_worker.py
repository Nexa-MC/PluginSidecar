#!/usr/bin/env python3
"""Package a single NativeAOT Worker image containing its native and UI dependencies."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
RIDS = ('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')


def package(rid, publish, output, signing_key=None):
    binary = publish / ('PluginSidecar.exe' if rid.startswith('win-') else 'PluginSidecar')
    if binary.is_symlink() or not binary.is_file():
        raise ValueError('Expected a regular NativeAOT Worker executable')
    output.mkdir(parents=True, exist_ok=True)
    allowed = {'Nexa.Plugin.Sidecar.nsc', 'Nexa.Plugin.Sidecar.nsc.asc', 'package-info.json'}
    if any(path.name not in allowed or path.is_symlink() or not path.is_file() for path in output.iterdir()):
        raise ValueError('Worker output must contain only its image, signature and package metadata; choose a clean directory')
    image = output / 'Nexa.Plugin.Sidecar.nsc'
    signature = Path(str(image) + '.asc')
    info = output / 'package-info.json'
    # A previous signature must never survive replacement of its image.
    signature.unlink(missing_ok=True)
    info.unlink(missing_ok=True)
    shutil.copyfile(binary, image)
    image.chmod(0o755)
    if signing_key:
        subprocess.run(['gpg', '--batch', '--yes', '--armor', '--local-user', signing_key,
                        '--output', str(signature), '--detach-sign', str(image)], check=True)
    with image.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    info.write_text(json.dumps(dict(schema=1, runtime=rid, worker=image.name,
        signed=bool(signing_key), files={image.name: dict(sha256=digest, size=image.stat().st_size)}), indent=2) + '\n', encoding='utf-8')
    return image


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rid', required=True, choices=RIDS)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--signing-key')
    parser.add_argument('--publish-directory', type=Path, help='Package a previously published NativeAOT output')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    output = args.output or ROOT / 'artifacts/worker' / args.rid
    with tempfile.TemporaryDirectory(prefix='nexa-worker-publish-') as temporary:
        publish = args.publish_directory or Path(temporary)
        if args.publish_directory is None:
            subprocess.run([args.dotnet, 'publish', str(ROOT / 'src/PluginSidecar'), '-c', 'Release',
                            '-r', args.rid, '-p:PublishAot=true', '-o', str(publish)], check=True)
        image = package(args.rid, publish, output, args.signing_key)
    print(image)
    if not args.signing_key:
        print('Unsigned development artifact; production Host admission requires its release signature.')
