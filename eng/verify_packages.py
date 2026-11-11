#!/usr/bin/env python3
"""Build and run a NuGet-only consumer outside the checkout, using an isolated cache."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--dotnet', default='dotnet')
parser.add_argument('--packages', type=Path, default=root / 'artifacts/packages')
parser.add_argument('--sample', type=Path, default=root / 'artifacts/wasm-sample/plugin.zip')
args = parser.parse_args()
version = ET.parse(root / 'src/Nexa.Plugin.Sdk.Abi100/Nexa.Plugin.Sdk.Abi100.csproj').findtext('.//Version')
with tempfile.TemporaryDirectory(prefix='nexa-sdk-consumer-') as directory:
    work = Path(directory)
    project = ET.Element('Project', Sdk='Microsoft.NET.Sdk')
    properties = ET.SubElement(project, 'PropertyGroup')
    for key, value in [('OutputType', 'Exe'), ('TargetFramework', 'net10.0'),
                       ('ImplicitUsings', 'enable'), ('Nullable', 'enable'), ('TreatWarningsAsErrors', 'true')]:
        ET.SubElement(properties, key).text = value
    references = ET.SubElement(project, 'ItemGroup')
    for name in ['PluginSidecar.Packages', 'PluginSidecar.RuntimeAdapter']:
        if not (args.packages / f'{name}.{version}.nupkg').is_file():
            raise SystemExit(f'Missing package: {name}.{version}.nupkg; run dotnet pack first.')
        ET.SubElement(references, 'PackageReference', Include=name, Version=version)
    ET.ElementTree(project).write(work / 'Consumer.csproj', encoding='unicode')
    config = ET.Element('configuration')
    sources = ET.SubElement(config, 'packageSources')
    ET.SubElement(sources, 'clear')
    ET.SubElement(sources, 'add', key='local-sdk', value=str(args.packages.resolve()))
    ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
    ET.ElementTree(config).write(work / 'NuGet.Config', encoding='unicode')
    shutil.copyfile(root / 'tests/PackageConsumer/Program.cs', work / 'Program.cs')
    environment = dict(os.environ, NUGET_PACKAGES=str(work / 'packages'))
    subprocess.run([args.dotnet, 'restore', 'Consumer.csproj', '--configfile', 'NuGet.Config'], cwd=work, env=environment, check=True)
    subprocess.run([args.dotnet, 'run', '--project', 'Consumer.csproj', '-c', 'Release', '--no-restore', '--', str(args.sample.resolve())],
                   cwd=work, env=environment, check=True)
