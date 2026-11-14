#!/usr/bin/env python3
"""Reject shared Host package drift, including test projects, before cache reuse."""
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def verify(root):
    expected = '[' + json.loads((root / 'eng/references/nexacl.json').read_text())['packagesVersion'] + ']'
    failures = []
    count = 0
    for folder in ('src', 'tests', 'samples'):
        for project in sorted((root / folder).rglob('*.csproj')):
            if {'obj', 'bin'} & set(project.relative_to(root).parts):
                continue
            for item in ET.parse(project).getroot().iter('PackageReference'):
                name = item.get('Include', item.get('Update', ''))
                if not name.startswith('Nexa.') or name.startswith('Nexa.Plugin.'):
                    continue
                count += 1
                version = item.get('Version', item.findtext('Version'))
                if version != expected:
                    failures.append(f'{project.relative_to(root)}: {name} uses {version!r}; expected {expected}')
    if failures:
        raise ValueError('Host package references differ from eng/references/nexacl.json:\n' + '\n'.join(failures))
    return count


if __name__ == '__main__':
    try:
        count = verify(Path(__file__).resolve().parents[1])
    except ValueError as error:
        raise SystemExit(str(error)) from error
    print(f'PASS: {count} direct shared Host references match the pinned package version.')
