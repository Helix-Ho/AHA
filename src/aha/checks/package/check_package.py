"""Packaging policy: check source resources and the complete built Yak payload."""
from pathlib import Path, PurePosixPath
import json
import sys
import re
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[4]
COMMANDS = ('AHA', 'AHATopo', 'AHAInfo', 'AHAEumCrawl', 'AHASetting')
RESOURCES = ('Aha.rhp', 'Aha.rui', 'Aha.deps.json', 'Aha.runtimeconfig.json', 'LICENSE', 'THIRD_PARTY_NOTICES.txt')


def manifest_scalar(text, key):
    """Read the simple top-level scalars emitted by this project's Yak manifest."""
    values = re.findall(r'^' + re.escape(key) + r':[ \t]*(.*)$', text, re.MULTILINE)
    assert len(values) == 1, 'manifest must contain exactly one ' + key
    value = values[0].strip()
    # These fields contain no multiline values or YAML tags. Accept Yak's plain
    # scalar and quoted forms without adding a general YAML parser dependency.
    match = re.fullmatch(r'''(?:"([^"\\]*)"|'([^']*)'|([^\s#]+))(?:[ \t]+\#.*)?''', value)
    assert match, 'invalid manifest scalar: ' + key
    return next(part for part in match.groups() if part is not None)


def check_toolbar(path):
    toolbar = ET.parse(path)
    macros = {item.attrib['guid']: item.findtext('script', '')
              for item in toolbar.findall('./macros/macro_item')}
    references = [item.text for item in toolbar.findall('./tool_bars/tool_bar/tool_bar_item/left_macro_id')]
    assert references and all(item in macros for item in references), 'toolbar references an absent command macro'
    commands = {token.lstrip('_') for item in references
                for token in re.findall(r'\b_?[A-Za-z][A-Za-z0-9_]*\b', macros[item])}
    for name in COMMANDS:
        assert name in commands, 'toolbar command missing: ' + name


def package_payload(build, version):
    """The dependency manifest defines runtime DLLs; unrelated build files are not payload."""
    deps = json.loads((build / 'Aha.deps.json').read_text())
    target = deps['targets'][deps['runtimeTarget']['name']]
    assert 'Aha/' + version in target, 'dependency manifest product version mismatch'
    runtime_files = {PurePosixPath(path).name for library in target.values()
                     for path in library.get('runtime', {})}
    assert 'Aha.rhp' in runtime_files, 'dependency manifest product runtime missing'
    assert all(name.endswith(('.rhp', '.dll')) for name in runtime_files), 'unexpected runtime dependency type'
    expected = set(RESOURCES) | runtime_files
    runtime = json.loads((build / 'Aha.runtimeconfig.json').read_text())
    assert runtime['runtimeOptions']['tfm'] == 'net8.0', 'runtime framework mismatch'
    for name in expected:
        assert (build / name).is_file() and (build / name).stat().st_size > 0, 'build payload missing or empty: ' + name
    return expected


def check(archive=None, root=ROOT):
    root = Path(root)
    version = ET.parse(root / 'Directory.Build.props').findtext('.//Version')
    assert version and re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version), 'invalid product version'
    source_package = root / 'src/aha/package'
    manifest = (source_package / 'manifest.yml').read_text()
    assert manifest_scalar(manifest, 'version') == '@AHA_VERSION@', 'manifest must use shared build version'
    assert manifest_scalar(manifest, 'name') == 'aha', 'manifest product name mismatch'
    check_toolbar(source_package / 'Aha.rui')
    if archive:
        build = root / 'src/aha/rhino/bin/Release/net8.0'
        expected = package_payload(build, version)
        with zipfile.ZipFile(archive) as package:
            names = package.namelist()
            assert len(names) == len(set(names)), 'duplicate package entry'
            assert set(names) == expected | {'manifest.yml'}, 'package has missing or unexpected payload files'
            # Exact root-level payload membership also excludes dev/docs, source,
            # tests, .env/KEY files and disguised nested or traversal paths.
            data = package.read('manifest.yml').decode('utf-8')
            assert manifest_scalar(data, 'name') == 'aha', 'package product name mismatch'
            assert manifest_scalar(data, 'version') == version, 'package version mismatch'
            for name in expected:
                assert package.read(name) == (build / name).read_bytes(), 'package file differs from build: ' + name
            assert package.read('Aha.rui') == (source_package / 'Aha.rui').read_bytes(), 'packaged toolbar differs from source'
            assert package.read('LICENSE') == (root / 'LICENSE').read_bytes(), 'packaged license differs from source'
            notices = root / 'src/topo/api/rhino/THIRD_PARTY_NOTICES.txt'
            assert package.read('THIRD_PARTY_NOTICES.txt') == notices.read_bytes(), 'packaged notices differ from source'
    print('PASS package manifest, toolbar, version' + (' and archive contents' if archive else ''))


if __name__ == '__main__':
    check(sys.argv[1] if len(sys.argv) > 1 else None)
