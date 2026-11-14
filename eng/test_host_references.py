#!/usr/bin/env python3
"""Exercise drift hidden by a warm feed/cache, including tests and sample projects."""
import json
from pathlib import Path
import tempfile
import unittest
from verify_host_references import verify


class ReferencesTests(unittest.TestCase):
    def test_scans_every_source_owner_before_cached_feed_reuse(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            pin = root / 'eng/references/nexacl.json'
            pin.parent.mkdir(parents=True)
            pin.write_text(json.dumps({'packagesVersion': '0.1.0-alpha.17'}))
            for folder in ('src', 'tests', 'samples'):
                project = root / folder / 'Example/Example.csproj'
                project.parent.mkdir(parents=True)
                for version in ('[0.1.0-alpha.16]', '0.1.0-alpha.17', ''):
                    project.write_text(f'<Project><ItemGroup><PackageReference Include="Nexa.Xsr.Runtime" Version="{version}" /></ItemGroup></Project>')
                    with self.assertRaisesRegex(ValueError, 'Example.csproj'):
                        verify(root)
                project.write_text('<Project><ItemGroup><PackageReference Include="Nexa.Xsr.Runtime" Version="[0.1.0-alpha.17]" /></ItemGroup></Project>')
            self.assertEqual(verify(root), 3)
            sample = root / 'samples/Example/Example.csproj'
            sample.write_text('<Project><ItemGroup><PackageReference Include="Nexa.Plugin.Bootstrap.V1" Version="0.1.0" /></ItemGroup></Project>')
            self.assertEqual(verify(root), 2)


if __name__ == '__main__':
    unittest.main()
