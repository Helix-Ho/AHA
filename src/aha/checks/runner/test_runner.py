"""Execution contract checks, independent of installed Rhino and user data."""
import json
import os
from pathlib import Path
import runpy
import sys
import tempfile
import time
import types
import unittest
from unittest.mock import patch
from runner import catalogue, select, outcome, run_process, ROOT, TOOLS
from catalogue import validate_local_fixture_set
from outcome import host_result
import runner


class RhinoFixture:
    """Only the runner's host boundary is simulated; no AHA business code is replaced."""
    def __init__(self, root):
        self.root = root
        self.store, self.methods, self.loaded = {}, {}, {}
        self.messages, self.handlers = [], []
        self.now = 0
        owner = self
        class Idle:
            def __iadd__(self, handler): owner.handlers.append(handler); return self
            def __isub__(self, handler): owner.handlers.remove(handler); return self
        self.app = types.SimpleNamespace(InvokeRequired=False, Version='Rhino fixture', Idle=Idle(), WriteLine=self.messages.append)
        class Array:
            def __getitem__(self, _): return list
        class Method:
            def __init__(self, callback): self.callback = callback
            def Invoke(self, _, arguments): return self.callback(*(arguments or []))
        class Assembly:
            def __init__(self, path, identity):
                self.Location = str(path)
                self.ManifestModule = types.SimpleNamespace(ModuleVersionId=identity)
            def GetName(self): return types.SimpleNamespace(Name='Aha', Version='0.0.1')
            def GetType(self, name):
                callback = (lambda path: Path(path).read_text()) if name == 'Aha.Checks.BuildIdentity' else owner.methods[name]
                return types.SimpleNamespace(GetMethod=lambda _: Method(callback))
        def load(path):
            if path not in self.loaded: self.loaded[path] = Assembly(path, Path(path).read_text())
            return self.loaded[path]
        self.product_path = root/'src/aha/rhino/bin/Release/net8.0/Aha.rhp'
        self.product_path.parent.mkdir(parents=True)
        self.product_path.write_text('product')
        product = load(str(self.product_path))
        domain = types.SimpleNamespace(GetData=lambda key: self.store.get(key), SetData=lambda key, value: self.store.update({key:value}), GetAssemblies=lambda:[product])
        system = types.ModuleType('System')
        system.AppDomain = types.SimpleNamespace(CurrentDomain=domain)
        system.Array, system.Object = Array(), object
        reflection = types.ModuleType('System.Reflection')
        reflection.Assembly = types.SimpleNamespace(LoadFrom=load)
        self.modules = {'System':system, 'System.Reflection':reflection, 'Rhino':types.SimpleNamespace(RhinoApp=self.app)}
        for tool in TOOLS:
            folder = root/'src'/tool/'checks/native'; folder.mkdir(parents=True)
            name = 'Aha.RuntimeNativeChecks' if tool == 'aha' else 'Aha.'+tool+'Checks'
            project = folder/(name+'.csproj'); project.write_text('<Project/>')
            dll = folder/'bin/Release/net8.0'/(name+'.dll'); dll.parent.mkdir(parents=True); dll.write_text(tool)
            entry = {'id':tool+'-native','kind':'rhino','layer':'rhino','path':str(project.relative_to(root)), 'type':tool, 'method':'RunSafe'}
            (folder.parent/'suites.json').write_text(json.dumps([entry]))
            self.methods[tool] = lambda: json.dumps({'success':True,'assertions':1})

    def namespace(self):
        with patch.dict(sys.modules, self.modules):
            namespace = runpy.run_path(str(ROOT/'src/aha/checks/run_rhino.py'), run_name='runner_fixture')
        globals_ = namespace['main'].__globals__
        globals_['__file__'] = str(self.root/'src/aha/checks/run_rhino.py')
        globals_['time'] = types.SimpleNamespace(time=lambda:self.now)
        return namespace

    def asynchronous(self, tool, callback):
        file = self.root/'src'/tool/'checks/suites.json'
        entry = json.loads(file.read_text())[0]
        entry.update(method='Start', timeout_seconds=1)
        file.write_text(json.dumps([entry]))
        self.methods[tool] = callback

    def pump(self):
        for callback in list(self.handlers): callback(None, None)

    def report(self, path): return json.loads(Path(path).read_text())

class RunnerChecks(unittest.TestCase):
    def test_catalogue_is_complete(self):
        entries = catalogue()
        self.assertTrue(all(select(entries, tool) for tool in TOOLS))
        self.assertEqual(len({s['id'] for s in entries}), len(entries))
        self.assertEqual([s['id'] for s in select(entries, layer='all')], [s['id'] for s in entries])
        selected = select(entries, layer='all', include_local_fixtures=True)
        self.assertEqual(len(selected), len(entries) + sum(bool(s.get('local_fixtures')) for s in entries))
        self.assertEqual([s['id'] for s in selected if s['scope'] == 'recorded'],
                         [s['id']+'-recorded' for s in entries if s.get('local_fixtures')])
        self.assertEqual(next(s['args'] for s in selected if s['id'] == 'geometry'), ['All'])
        self.assertEqual(next(s['args'] for s in selected if s['id'] == 'geometry-recorded'), ['Recorded'])
        with self.assertRaises(ValueError): select(entries, 'missing')
        with self.assertRaises(ValueError): select([], 'all')

    def test_aggregate_never_accepts_pending_empty_or_unavailable(self):
        for states, expected in [([], ('failed',1)), (['running'],('failed',1)),
                (['passed'],('passed',0)), (['passed','unavailable'],('unavailable',2)),
                (['failed','unavailable'],('failed',1))]:
            self.assertEqual(outcome([{'status':s} for s in states]), expected)

    def test_process_error_and_timeout(self):
        import sys, os
        with tempfile.TemporaryDirectory() as tmp:
            log=Path(tmp)/'log'
            self.assertEqual(run_process([sys.executable,'-c','raise SystemExit(1)'],log,os.environ.copy(),10)['status'],'failed')
            self.assertEqual(run_process([str(Path(tmp)/'missing')],log,os.environ.copy(),10)['status'],'unavailable')
            self.assertEqual(run_process([sys.executable,'-c','import time;time.sleep(20)'],log,os.environ.copy(),0.1)['status'],'failed')

    def test_catalogue_rejects_duplicate_and_missing_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)
            for tool in TOOLS:
                folder=root/'src'/tool/'checks';folder.mkdir(parents=True)
                file=folder/'case.py';file.write_text('')
                (folder/'suites.json').write_text(json.dumps([{'id':tool,'kind':'python','layer':'offline','path':str(file.relative_to(root))}]))
            self.assertEqual(len(catalogue(root)),5)
            p=root/'src/aha/checks/suites.json'; entry=json.loads(p.read_text());entry[0]['id']='setting';p.write_text(json.dumps(entry))
            with self.assertRaises(ValueError): catalogue(root)
            entry[0]['id']='aha';entry[0]['path']='missing';p.write_text(json.dumps(entry))
            with self.assertRaises((ValueError, IOError)): catalogue(root)
            entry[0]['path']='src/aha/checks/case.py';p.write_text(json.dumps(entry))
            stray=root/'src/aha/checks/Unregistered.csproj';stray.write_text('<Project/>')
            with self.assertRaises(ValueError): catalogue(root)
            stray.unlink()
            entry.append(dict(entry[0],id='duplicate-path'));p.write_text(json.dumps(entry))
            with self.assertRaises(ValueError): catalogue(root)
            entry.pop();entry[0]['local_fixtures']=True;p.write_text(json.dumps(entry))
            self.assertEqual(len(catalogue(root)),5)
            self.assertEqual(len(select(catalogue(root),layer='all',include_local_fixtures=True)),6)
            entry[0]['scope']='recorded';p.write_text(json.dumps(entry))
            with self.assertRaises(ValueError): catalogue(root)

    def test_recorded_fixture_preflight_and_scope_selection(self):
        import hashlib
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); directory=root/'.local/test-data/topo';directory.mkdir(parents=True)
            data=directory/'sample.json';data.write_bytes(b'{"x":1}')
            manifest=directory/'file-sha256.json'
            manifest.write_text(json.dumps({'sample.json':hashlib.sha256(data.read_bytes()).hexdigest()}))
            self.assertEqual(validate_local_fixture_set(root,'topo'),str(directory))
            data.write_bytes(b'{"x":2}')
            with self.assertRaises(IOError): validate_local_fixture_set(root,'topo')
            data.unlink()
            with self.assertRaises(IOError): validate_local_fixture_set(root,'topo')

    def test_python_and_node_registration_and_owner_validation(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            fixture = RhinoFixture(root)
            for name in ('test_missing.py', 'check_missing.py', 'missing-checks.cjs'):
                missing = root/'src/aha/checks'/name
                missing.write_text('')
                with self.assertRaises(ValueError): catalogue(root)
                missing.unlink()
            helper = root/'src/aha/checks/helper.py'; helper.write_text('')
            self.assertEqual(len(catalogue(root)), 5)
            file = root/'src/aha/checks/suites.json'
            original = json.loads(file.read_text())[0]
            for changes in ({'kind':'python'}, {'layer':'offline'}, {'path':'../escape.py'},
                            {'path':'src/info/checks/native/Aha.infoChecks.csproj'},
                            {'args':'not-an-array'}, {'timeout_seconds':0}, {'timeout_seconds':float('inf')},
                            {'method':'Unknown'}, {'id':'../unsafe'}, {'local_fixtures':'yes'},
                            {'local_args':['Recorded']}):
                file.write_text(json.dumps([dict(original, **changes)]))
                with self.assertRaises((ValueError, IOError)): catalogue(root)
            file.write_text(json.dumps([original]))

    def test_host_report_requires_complete_assertions(self):
        for value in (None, [], {'status':'passed'}, {'success':True,'assertions':0},
                      {'status':'passed','cases':[{'success':False}]}, {'status':'running'},
                      {'status':'passed','success':False,'assertions':1}):
            self.assertEqual(host_result(value), 'failed')
        self.assertEqual(host_result({'status':'unavailable'}), 'unavailable')
        self.assertEqual(host_result({'status':'passed','cases':[{'status':'passed'}]}), 'passed')
        self.assertEqual(host_result({'success':True,'assertions':3}), 'passed')

    def test_terminal_invalid_setup_replaces_previous_success(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(runner, 'ROOT', Path(tmp)):
            report = Path(tmp)/'outputs/checks/offline.json'
            report.parent.mkdir(parents=True); report.write_text('{"status":"passed"}')
            def invalid():
                self.assertEqual(json.loads(report.read_text())['status'], 'running')
                raise ValueError('Invalid registered check')
            with patch.object(runner, 'catalogue', invalid):
                self.assertEqual(runner.main(argv=[]), 2)
            self.assertEqual(json.loads(report.read_text())['status'], 'unavailable')
            self.assertFalse((report.parent/'terminal.lock').exists())

    def test_explicit_dotnet_override_is_not_silently_replaced(self):
        with patch.dict(os.environ, {'AHA_DOTNET':'/missing/explicit/dotnet'}), patch.object(runner.shutil, 'which', return_value=None):
            self.assertEqual(runner.environment()[0], '/missing/explicit/dotnet')

    def test_python_and_node_receive_their_registered_arguments(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(runner, 'ROOT', Path(tmp)):
            suites = [dict(id=kind, tool='aha', kind=kind, layer='offline', path='check.'+kind, args=['--fixture', 'one']) for kind in ('python', 'node')]
            commands = []
            def execute(command, *args):
                commands.append(command)
                return {'status':'passed','exit_code':0}
            with patch.object(runner, 'catalogue', return_value=suites), patch.object(runner, 'run_process', execute):
                self.assertEqual(runner.main(argv=[]), 0)
            self.assertTrue(all(command[-2:] == ['--fixture','one'] for command in commands))

    def test_listing_invalid_catalogue_does_not_overwrite_a_run(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(runner, 'ROOT', Path(tmp)):
            report = Path(tmp)/'outputs/checks/offline.json'; report.parent.mkdir(parents=True)
            report.write_text('{"status":"running","run_id":"active"}')
            with patch.object(runner, 'catalogue', side_effect=ValueError('invalid')):
                self.assertEqual(runner.main(argv=['--list']), 2)
            self.assertEqual(json.loads(report.read_text())['run_id'], 'active')

    def test_terminal_concurrent_invocation_does_not_replace_active_report(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(runner, 'ROOT', Path(tmp)):
            output = Path(tmp)/'outputs/checks'; output.mkdir(parents=True)
            report = output/'offline.json'; report.write_text('{"status":"running","run_id":"active"}')
            runner.claim_run(output/'terminal.lock')
            self.assertEqual(runner.main(argv=[]), 2)
            self.assertEqual(json.loads(report.read_text())['run_id'], 'active')
            rejected = list(output.glob('rejected-*.json'))
            self.assertEqual(len(rejected), 1)
            self.assertEqual(json.loads(rejected[0].read_text())['exit_code'], 2)

    def test_non_posix_lock_check_never_signals_a_process(self):
        with tempfile.TemporaryDirectory() as tmp:
            lock = Path(tmp)/'terminal.lock'
            lock.write_text(json.dumps({'pid':os.getpid()}))
            with patch.object(runner.os, 'name', 'nt'), patch.object(runner.os, 'kill') as kill:
                with self.assertRaises(RuntimeError): runner.claim_run(lock)
                kill.assert_not_called()

    @unittest.skipUnless(os.name == 'posix', 'Current host uses POSIX process groups')
    def test_timeout_stops_child_processes(self):
        with tempfile.TemporaryDirectory() as tmp:
            marker = Path(tmp)/'survived'
            ready = Path(tmp)/'child-started'
            child = 'import time,pathlib;pathlib.Path('+repr(str(ready))+').write_text("ready");time.sleep(0.5);pathlib.Path('+repr(str(marker))+').write_text("leaked")'
            script = 'import subprocess,sys,time;subprocess.Popen([sys.executable,"-c",'+repr(child)+']);time.sleep(20)'
            self.assertEqual(run_process([sys.executable,'-c',script],Path(tmp)/'log',os.environ.copy(),0.2)['status'],'failed')
            self.assertTrue(ready.exists(), 'Child fixture must actually start before timeout')
            time.sleep(0.6)
            self.assertFalse(marker.exists())

    def test_rhino_invalid_selection_replaces_old_success(self):
        with tempfile.TemporaryDirectory() as tmp:
            fixture = RhinoFixture(Path(tmp))
            output = Path(tmp)/'outputs/checks/rhino'; output.mkdir(parents=True)
            (output/'report.json').write_text('{"status":"passed"}')
            path = fixture.namespace()['main']('missing')
            self.assertEqual(fixture.report(path)['status'], 'unavailable')
            self.assertEqual(fixture.report(output/'report.json')['status'], 'unavailable')
            self.assertFalse(any(fixture.store.values()))

    def test_rhino_uses_same_registration_validation(self):
        with tempfile.TemporaryDirectory() as tmp:
            fixture = RhinoFixture(Path(tmp))
            (Path(tmp)/'src/topo/checks/check_unregistered.py').write_text('')
            with self.assertRaises(ValueError): catalogue(Path(tmp))
            report = fixture.report(fixture.namespace()['main']('info'))
            self.assertEqual(report['status'], 'unavailable')
            self.assertIn('Unregistered', report['error'])

    def test_rhino_current_build_and_success(self):
        with tempfile.TemporaryDirectory() as tmp:
            fixture = RhinoFixture(Path(tmp))
            path = fixture.namespace()['main']('info')
            fixture.pump(); fixture.pump()
            self.assertEqual(fixture.report(path)['status'], 'passed')
            fixture.product_path.write_text('changed-after-load')
            newer = fixture.namespace()['main']('info')
            self.assertEqual(fixture.report(newer)['status'], 'unavailable')
            self.assertIn('differs from build', fixture.report(newer)['error'])

    def test_rhino_reentry_across_script_instances_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            fixture = RhinoFixture(Path(tmp))
            first = fixture.namespace()['main']('info')
            second = fixture.namespace()['main']('info')
            self.assertNotEqual(first, second)
            self.assertEqual(fixture.report(second)['status'], 'unavailable')
            fixture.pump(); fixture.pump()
            self.assertEqual(fixture.report(first)['status'], 'passed')

    def test_rhino_cannot_accept_a_report_from_another_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            fixture = RhinoFixture(Path(tmp))
            old = Path(tmp)/'old.json'; old.write_text('{"status":"passed","assertions":1}')
            fixture.asynchronous('info', lambda output:str(old))
            path = fixture.namespace()['main']('info')
            fixture.pump(); fixture.pump()
            self.assertEqual(fixture.report(path)['status'], 'failed')
            self.assertIn('outside this run', fixture.report(path)['cases'][0]['error'])

    def test_rhino_timeout_keeps_ownership_until_actual_completion(self):
        with tempfile.TemporaryDirectory() as tmp:
            fixture = RhinoFixture(Path(tmp)); pending = []
            def start(output):
                path = Path(output)/'current.json'; path.write_text('{"status":"running"}'); pending.append(path)
                return str(path)
            fixture.asynchronous('info', start)
            path = fixture.namespace()['main']('info')
            fixture.pump()
            fixture.now = 2; fixture.pump()
            self.assertEqual(fixture.report(path)['status'], 'failed')
            self.assertEqual(fixture.report(fixture.namespace()['main']('info'))['status'], 'unavailable')
            pending[0].write_text('{"status":"passed","assertions":1}')
            fixture.pump()
            self.assertFalse(any(fixture.store.values()))
            self.assertEqual(fixture.report(path)['status'], 'failed')

if __name__ == '__main__': unittest.main()
