"""Deterministic package-policy regressions using isolated build files and ZIPs."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
import warnings
import zipfile

import check_package


class PackagePolicyChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='aha-package-check-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / 'src/aha/package'
        self.build = self.root / 'src/aha/rhino/bin/Release/net8.0'
        self.source.mkdir(parents=True)
        self.build.mkdir(parents=True)
        (self.root / 'Directory.Build.props').write_text('<Project><PropertyGroup><Version>0.0.1</Version></PropertyGroup></Project>')
        (self.source / 'manifest.yml').write_text('name: aha\nversion: @AHA_VERSION@\n')
        self.toolbar = self.toolbar_bytes(check_package.COMMANDS)
        (self.source / 'Aha.rui').write_bytes(self.toolbar)
        notices = self.root / 'src/topo/api/rhino/THIRD_PARTY_NOTICES.txt'
        notices.parent.mkdir(parents=True)
        notices.write_bytes(b'Fixture third-party notice\n')
        (self.root / 'LICENSE').write_bytes(b'Fixture project license\n')
        deps = {'runtimeTarget': {'name': 'test-target'}, 'targets': {'test-target': {
            'Aha/0.0.1': {'runtime': {'Aha.rhp': {}}},
            'FixtureDependency/1.0.0': {'runtime': {'lib/net8.0/FixtureDependency.dll': {}}}
        }}}
        self.files = {
            'Aha.rhp': b'fixture product assembly',
            'Aha.rui': self.toolbar,
            'Aha.deps.json': json.dumps(deps).encode(),
            'Aha.runtimeconfig.json': b'{"runtimeOptions":{"tfm":"net8.0"}}',
            'FixtureDependency.dll': b'fixture runtime dependency',
            'THIRD_PARTY_NOTICES.txt': notices.read_bytes(),
            'LICENSE': (self.root / 'LICENSE').read_bytes(),
        }
        for name, data in self.files.items():
            (self.build / name).write_bytes(data)
        self.files['manifest.yml'] = b'name: aha\nversion: 0.0.1\n'

    @staticmethod
    def toolbar_bytes(commands):
        macros = ''.join('<macro_item guid="m{0}"><script>! _{1}</script></macro_item>'.format(i, name)
                         for i, name in enumerate(commands))
        buttons = ''.join('<tool_bar_item><left_macro_id>m{0}</left_macro_id></tool_bar_item>'.format(i)
                          for i in range(len(commands)))
        return ('<RhinoUI><macros>' + macros + '</macros><tool_bars><tool_bar>' + buttons +
                '</tool_bar></tool_bars></RhinoUI>').encode()

    def archive(self, files=None, duplicate=None):
        path = self.root / 'fixture.yak'
        with zipfile.ZipFile(path, 'w') as package:
            for name, data in (self.files if files is None else files).items():
                package.writestr(name, data)
            if duplicate:
                with warnings.catch_warnings():
                    warnings.simplefilter('ignore', UserWarning)
                    package.writestr(duplicate, self.files[duplicate])
        return path

    def inspect(self, archive):
        with contextlib.redirect_stdout(io.StringIO()):
            check_package.check(archive, root=self.root)

    def test_valid_payload_and_manifest_quote_forms(self):
        for version in ('0.0.1', '"0.0.1"', "'0.0.1'", '0.0.1 # current version'):
            with self.subTest(version=version):
                self.inspect(self.archive(dict(self.files, **{'manifest.yml': ('name: aha\nversion: ' + version + '\n').encode()})))

    def test_development_source_and_secret_files_are_not_payload(self):
        for name in ('dev/aha/HISTORY.md', 'docs/topo/README.md', 'AGENTS.md', 'README.md',
                     '.env', '.env.local', 'VWorld_KEY.txt', 'settings.json', 'Aha.Checks.dll',
                     'src/tool.cs', '../Aha.rhp', '/Aha.rhp', 'nested/Aha.rhp'):
            with self.subTest(name=name), self.assertRaises(AssertionError):
                self.inspect(self.archive(dict(self.files, **{name: b'unexpected content'})))

    def test_every_runtime_resource_dependency_and_manifest_is_required(self):
        for name in self.files:
            with self.subTest(name=name), self.assertRaises(AssertionError):
                self.inspect(self.archive({key: value for key, value in self.files.items() if key != name}))

    def test_duplicate_or_nested_payload_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.inspect(self.archive(duplicate='Aha.rhp'))
        nested = dict(self.files)
        nested['nested/Aha.rhp'] = nested.pop('Aha.rhp')
        with self.assertRaises(AssertionError):
            self.inspect(self.archive(nested))

    def test_version_and_name_are_exact_unique_scalars(self):
        for manifest in ('name: aha\nversion: 0.0.10\n', 'name: aha\nversion: 0.0.1-other\n',
                         'name: other\nversion: 0.0.1\n', 'name: aha\n# version: 0.0.1\n',
                         'name: aha\nversion: 0.0.1\nversion: 0.0.2\n'):
            with self.subTest(manifest=manifest), self.assertRaises(AssertionError):
                self.inspect(self.archive(dict(self.files, **{'manifest.yml': manifest.encode()})))

    def test_payload_matches_the_current_build(self):
        for name in self.files.keys() - {'manifest.yml'}:
            with self.subTest(name=name), self.assertRaises(AssertionError):
                self.inspect(self.archive(dict(self.files, **{name: b'outdated or damaged payload'})))

    def test_build_resources_must_match_source_and_be_available(self):
        for name in ('Aha.rui', 'LICENSE', 'THIRD_PARTY_NOTICES.txt'):
            with self.subTest(name=name):
                previous = (self.build / name).read_bytes()
                (self.build / name).write_bytes(b'outdated build resource')
                try:
                    with self.assertRaises(AssertionError):
                        self.inspect(self.archive(dict(self.files, **{name: b'outdated build resource'})))
                finally:
                    (self.build / name).write_bytes(previous)
        dependency = self.build / 'FixtureDependency.dll'
        dependency.write_bytes(b'')
        with self.assertRaises(AssertionError):
            self.inspect(self.archive())
        dependency.unlink()
        with self.assertRaises(AssertionError):
            self.inspect(self.archive())

    def test_toolbar_requires_each_referenced_command_not_substrings(self):
        # AHATopo etc. still contain "AHA", but do not replace the AHA command.
        (self.source / 'Aha.rui').write_bytes(self.toolbar_bytes(check_package.COMMANDS[1:]))
        with self.assertRaises(AssertionError):
            self.inspect(None)
        (self.source / 'Aha.rui').write_bytes(self.toolbar.replace(b'<left_macro_id>m0</left_macro_id>',
                                                                b'<left_macro_id>absent</left_macro_id>'))
        with self.assertRaises(AssertionError):
            self.inspect(None)


if __name__ == '__main__':
    unittest.main()
