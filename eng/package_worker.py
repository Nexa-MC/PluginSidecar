#!/usr/bin/env python3
"""Package the NativeAOT worker and its native Wasmtime dependency for signed deployment."""
import argparse, hashlib, json, shutil, subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--rid',required=True);p.add_argument('--dotnet',default='dotnet');p.add_argument('--signing-key');args=p.parse_args()
if args.rid not in ['win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64']:raise SystemExit('Unsupported RID')
out=root/'artifacts/worker'/args.rid
subprocess.run([args.dotnet,'publish',str(root/'src/PluginSidecar'),'-c','Release','-r',args.rid,'-p:PublishAot=true','-o',str(out)],check=True)
binary=out/('PluginSidecar.exe' if args.rid.startswith('win-') else 'PluginSidecar')
package=out/'Nexa.Plugin.Sidecar.nsc';shutil.copyfile(binary,package)
signature=Path(str(package)+'.asc')
if signature.exists():signature.unlink()
if args.signing_key:
    subprocess.run(['gpg','--batch','--yes','--armor','--local-user',args.signing_key,'--output',str(signature),'--detach-sign',str(package)],check=True)
files={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in out.iterdir() if p.is_file() and p.name!='package-info.json'}
(out/'package-info.json').write_text(json.dumps(dict(runtime=args.rid,worker=package.name,signed=bool(args.signing_key),files=files),indent=2)+'\n')
print(package)
if not args.signing_key:print('Unsigned development artifact; production Host admission requires its release signature.')
