#!/usr/bin/env python3
"""Prepare pinned public Host protocol packages outside this repository's source graph."""
import argparse,json,os,subprocess,hashlib,sys
from pathlib import Path
root=Path(__file__).resolve().parents[1]
spec=json.loads((root/'eng/references/nexacl.json').read_text())
parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path);parser.add_argument('--dotnet',default='dotnet');args=parser.parse_args()
checkout=Path.home()/'.cache/nexa-references'/spec['commit']
feed=root/'artifacts/references/nexacl-feed'
manifest=feed/'feed-manifest.json'
if manifest.exists():
 data=json.loads(manifest.read_text())
 if data['source_commit']==spec['commit'] and all((feed/p['file']).is_file() and hashlib.sha256((feed/p['file']).read_bytes()).hexdigest()==p['sha256'] for p in data['packages']):
  print(feed);raise SystemExit(0)
checkout.parent.mkdir(parents=True,exist_ok=True)
if not (checkout/'.git').exists():
 subprocess.run(['git','clone','--no-checkout',str(args.source.resolve()) if args.source else spec['repository'],str(checkout)],check=True)
subprocess.run(['git','-C',str(checkout),'checkout','--detach',spec['commit']],check=True)
actual=subprocess.check_output(['git','-C',str(checkout),'rev-parse','HEAD'],text=True).strip()
if actual!=spec['commit']:raise SystemExit('Host reference does not match lock.')
environment=os.environ.copy()
if args.dotnet!='dotnet':environment['PATH']=str(Path(args.dotnet).resolve().parent)+os.pathsep+environment.get('PATH','')
# The pinned Host revision may allow SDK feature/patch roll-forward even though
# its package manifest requires exact compiler bytes. New runner images must not
# silently choose a newer SDK. Override SDK selection only for this build and
# restore the pinned source file even when the build fails.
selection=checkout/'global.json'
original=selection.read_bytes() if selection.exists() else None
package_spec=json.loads((checkout/'eng/packages/manifest.json').read_text())
if package_spec['version']!=spec['packagesVersion']:raise SystemExit('Host package version does not match lock.')
try:
 selection.write_text(json.dumps({'sdk':{'version':package_spec['sdk'],'rollForward':'disable','allowPrerelease':False}})+'\n')
 subprocess.run([sys.executable,'eng/packages/build.py','--output',str(feed)],cwd=checkout,env=environment,check=True)
finally:
 if original is None:selection.unlink(missing_ok=True)
 else:selection.write_bytes(original)
print(feed)
