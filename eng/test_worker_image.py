#!/usr/bin/env python3
"""Exercise a lone native Worker image and conservative extraction cleanup on this RID."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

PREFIX = 'NexaCL.PluginSidecar.native.'
MARKER = 'native.bundle.v1'
NATIVE = ('wasmtime.dll', 'libSkiaSharp.dll') if os.name == 'nt' else (
    ('libwasmtime.dylib', 'libSkiaSharp.dylib') if __import__('sys').platform == 'darwin' else ('libwasmtime.so', 'libSkiaSharp.so'))


def verify(image):
    with tempfile.TemporaryDirectory(prefix='nexa-worker-image-') as temporary:
        root = Path(temporary).resolve()
        image_directory = root / 'image'
        image_directory.mkdir()
        worker = image_directory / 'Nexa.Plugin.Sidecar.nsc'
        shutil.copy2(image, worker)
        worker.chmod(0o700)
        scratch = root / 'scratch'
        scratch.mkdir()
        env = dict(os.environ, TMP=str(scratch), TEMP=str(scratch), TMPDIR=str(scratch))
        env.pop('LD_LIBRARY_PATH', None)
        env.pop('DYLD_LIBRARY_PATH', None)

        def marked(name):
            folder = scratch / (PREFIX + name)
            folder.mkdir()
            (folder / MARKER).write_bytes(b'Nexa.Plugin.Native/1')
            (folder / NATIVE[0]).write_bytes(b'inactive fixture')
            return folder

        stale = marked('stale')
        unknown = marked('unknown')
        (unknown / 'user-data').write_bytes(b'preserve')
        invalid = marked('invalid')
        (invalid / MARKER).write_bytes(b'not ours')
        target = root / 'link-target'
        target.mkdir()
        (target / 'sentinel').write_bytes(b'preserve')
        link = scratch / (PREFIX + 'link')
        link_created = False
        try:
            link.symlink_to(target, target_is_directory=True)
            link_created = True
        except OSError as error:
            if os.name != 'nt' or getattr(error, 'winerror', None) != 1314:
                raise
            print('SKIP: creating Windows symbolic links requires unavailable privileges.')

        def smoke():
            result = subprocess.run([str(worker), '--native-runtime-smoke'], env=env,
                                    capture_output=True, text=True, timeout=30)
            if result.returncode or result.stdout.strip() != 'Native Worker libraries ready.' or result.stderr:
                raise AssertionError(f'Native smoke failed ({result.returncode}): {result.stdout} {result.stderr}')

        smoke()
        assert not stale.exists(), 'inactive owned extraction was not removed'
        assert (unknown / 'user-data').read_bytes() == b'preserve'
        assert (invalid / NATIVE[0]).exists()
        assert (target / 'sentinel').read_bytes() == b'preserve'
        if link_created:
            assert link.is_symlink(), 'cleanup removed the reparse-point fixture'
            assert link.resolve() == target

        before = set(scratch.iterdir())
        # Bootstrap waits for the host challenge after native resources have been leased.
        active = subprocess.Popen([str(worker), '--nexa-sidecar', '--endpoint', 'unused-native-package-test'],
                                  env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            deadline = time.monotonic() + 10
            while True:
                assert active.poll() is None, 'waiting Worker exited before its challenge'
                live = [path for path in set(scratch.iterdir()) - before if all((path / name).exists() for name in NATIVE)]
                if live:
                    break
                if time.monotonic() >= deadline:
                    raise AssertionError('Worker did not create its private native extraction')
                time.sleep(0.02)
            smoke()
            assert active.poll() is None
            assert all((live[0] / name).exists() for name in NATIVE), 'cleanup removed a live Worker library'
        finally:
            active.kill()
            active.communicate(timeout=10)
        smoke()
        assert not live[0].exists(), 'crashed Worker extraction was not reclaimed'
        assert list(image_directory.iterdir()) == [worker], 'Worker wrote dependencies beside its signed image'
    print('PASS: lone Worker executes Wasmtime and Skia; stale cleanup preserves live leases, unknown files and links.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image', type=Path)
    verify(parser.parse_args().image.resolve())
