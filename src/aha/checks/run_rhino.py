# Rhino RunPythonScript entry: no UI automation, key or external network.
# Python 2/3 compatible because Rhino 8's RunPythonScript can use IronPython.
import json, os, runpy, time, traceback, uuid
# Reload stateless helpers from this checkout, including after a prior script run.
_shared = runpy.run_path(os.path.join(os.path.dirname(__file__), 'runner', 'outcome.py'))
outcome, host_result = _shared['outcome'], _shared['host_result']
_shared = runpy.run_path(os.path.join(os.path.dirname(__file__), 'runner', 'catalogue.py'))
catalogue, select, inside = _shared['catalogue'], _shared['select'], _shared['inside']
validate_local_fixture_set = _shared['validate_local_fixture_set']
import System
from System.Reflection import Assembly
import Rhino

_RUN_SLOT = 'AHA.CoreChecks.ActiveRun'


def main(tool='all', include_local_fixtures=False):
    if Rhino.RhinoApp.InvokeRequired:
        raise RuntimeError('Run on the Rhino UI thread')
    root = os.path.abspath(os.path.join(os.path.dirname(__file__), '../../..'))
    output = os.path.join(root, 'outputs', 'checks', 'rhino')
    run_id = uuid.uuid4().hex
    run_output = os.path.join(output, 'runs', run_id)
    os.makedirs(run_output)
    report_path = os.path.join(run_output, 'report.json')
    latest_path = os.path.join(output, 'report.json')
    report = {'status': 'running', 'run_id': run_id, 'tool': tool,
              'include_local_fixtures': include_local_fixtures, 'cases': [], 'rhino': str(Rhino.RhinoApp.Version)}
    domain = System.AppDomain.CurrentDomain
    owns_run = not domain.GetData(_RUN_SLOT)
    if owns_run:
        domain.SetData(_RUN_SLOT, run_id)

    def save():
        for path in ([report_path, latest_path] if owns_run else [report_path]):
            temporary = path + '.tmp'
            with open(temporary, 'w') as stream:
                json.dump(report, stream, indent=2)
            if os.path.exists(path): os.remove(path)
            os.rename(temporary, path)

    def release():
        if domain.GetData(_RUN_SLOT) == run_id:
            domain.SetData(_RUN_SLOT, None)

    if not owns_run:
        report.update(status='unavailable', exit_code=2, error='Another Rhino check run is active; its report has not been replaced.')
        save()
        Rhino.RhinoApp.WriteLine('AHA checks unavailable: ' + report_path)
        return report_path

    suites = []
    state = {'index': 0, 'pending': None, 'started': time.time(), 'running_tick': False, 'quarantined': False}
    save()  # Invalid selection or setup must not leave an earlier passed report visible.
    try:
        suites = select(catalogue(root), tool, 'rhino', include_local_fixtures)
        product = next(a for a in domain.GetAssemblies() if a.GetName().Name == 'Aha')
        runtime_path = os.path.join(root, 'src/aha/checks/native/bin/Release/net8.0/Aha.RuntimeNativeChecks.dll')
        identity = Assembly.LoadFrom(runtime_path).GetType('Aha.Checks.BuildIdentity').GetMethod('ModuleId')
        def require_current(assembly, path):
            expected = str(identity.Invoke(None, System.Array[System.Object]([path])))
            if str(assembly.ManifestModule.ModuleVersionId) != expected:
                raise RuntimeError('Loaded module differs from build; restart Rhino: ' + path)
        require_current(product, os.path.join(root, 'src/aha/rhino/bin/Release/net8.0/Aha.rhp'))
        report['product'] = {'path': product.Location, 'version': str(product.GetName().Version), 'module_id': str(product.ManifestModule.ModuleVersionId)}
        require_current(Assembly.LoadFrom(runtime_path), runtime_path)
    except Exception as error:
        report.update(status='unavailable', exit_code=2, error=str(error))
        save(); release()
        Rhino.RhinoApp.WriteLine('AHA checks unavailable: ' + str(error))
        return report_path

    def finish(release_run=True):
        if release_run:
            Rhino.RhinoApp.Idle -= tick
            domain.SetData('AHA_CHECK_DATA_SCOPE', None)
            domain.SetData('AHA_CHECK_LOCAL_FIXTURES', None)
            release()
        reported = set(c['id'] for c in report['cases'])
        for suite in suites:
            if suite['id'] not in reported:
                report['cases'].append({'id': suite['id'], 'status': 'unavailable', 'error': 'Earlier host check did not finish safely'})
        report['status'], report['exit_code'] = outcome(report['cases'])
        report['elapsed_seconds'] = time.time() - state['started']
        save()
        Rhino.RhinoApp.WriteLine('AHA checks: ' + report['status'] + ' - ' + report_path)

    def read_pending(path):
        if not os.path.isfile(path): return None
        try:
            with open(path) as stream: return json.load(stream)
        except (ValueError, IOError): return None  # A check may still be writing the JSON.

    def tick(sender, event):
        if state['running_tick']: return
        state['running_tick'] = True
        try:
            if state['quarantined']:
                # Timing out the observer cannot cancel an arbitrary Rhino callback safely.
                # Keep ownership until its final report confirms completion (or Rhino exits).
                value = read_pending(state['pending'][0])
                if isinstance(value, dict) and value.get('status') in ('passed', 'failed', 'completed_with_failures', 'unavailable'):
                    Rhino.RhinoApp.Idle -= tick
                    domain.SetData('AHA_CHECK_DATA_SCOPE', None)
                    domain.SetData('AHA_CHECK_LOCAL_FIXTURES', None)
                    release()
                return
            if state['pending']:
                path, case, deadline = state['pending']
                if time.time() > deadline:
                    case.update(status='failed', error='Host check timed out; no further checks will start. Wait for cleanup or restart Rhino before retrying.')
                    state['quarantined'] = True
                    finish(False)
                    return
                result = read_pending(path)
                if result is None: return
                if isinstance(result, dict) and result.get('status') in ('starting', 'running'): return
                case['status'] = host_result(result)
                case['report'] = path
                domain.SetData('AHA_CHECK_DATA_SCOPE', None)
                domain.SetData('AHA_CHECK_LOCAL_FIXTURES', None)
                state['pending'] = None
                save()
            if state['index'] == len(suites):
                finish()
                return
            suite = suites[state['index']]
            state['index'] += 1
            case = {'id': suite['id'], 'status': 'running', 'tool': suite['tool'], 'layer': 'rhino',
                    'scope': suite.get('scope', 'public')}
            report['cases'].append(case)
            save()
            try:
                if suite.get('scope') == 'recorded':
                    validate_local_fixture_set(root, suite['tool'])
                    domain.SetData('AHA_CHECK_DATA_SCOPE', 'recorded')
                    domain.SetData('AHA_CHECK_LOCAL_FIXTURES', os.path.join(root, '.local', 'test-data'))
                else:
                    domain.SetData('AHA_CHECK_DATA_SCOPE', 'public')
                project = os.path.join(root, suite['path'])
                dll = os.path.join(os.path.dirname(project), 'bin', 'Release', 'net8.0', os.path.splitext(os.path.basename(project))[0] + '.dll')
                if not os.path.isfile(dll): raise IOError('Missing check DLL: ' + dll)
                assembly = Assembly.LoadFrom(dll)
                require_current(assembly, dll)
                method = assembly.GetType(suite['type']).GetMethod(suite['method'])
                arguments = list(suite.get('args', []))
                suite_output = os.path.join(run_output, suite['id'])
                if suite['method'] == 'Start':
                    os.mkdir(suite_output)
                    arguments.append(suite_output)
                raw = str(method.Invoke(None, System.Array[System.Object](arguments)))
                if suite['method'] == 'Start':
                    path = os.path.join(raw, 'report.json') if os.path.isdir(raw) else raw
                    if not inside(path, suite_output):
                        raise ValueError('Check returned a report outside this run: ' + path)
                    state['pending'] = (path, case, time.time() + suite.get('timeout_seconds', 1800))
                else:
                    value = json.loads(raw)
                    case['status'] = host_result(value)
                    case['result'] = value
                save()
            except Exception as error:
                case['status'] = 'unavailable' if isinstance(error, (IOError, RuntimeError)) else 'failed'
                case['error'] = str(error)
                case['traceback'] = traceback.format_exc()
                domain.SetData('AHA_CHECK_DATA_SCOPE', None)
                domain.SetData('AHA_CHECK_LOCAL_FIXTURES', None)
                save()
        finally:
            state['running_tick'] = False
    save()
    Rhino.RhinoApp.Idle += tick
    Rhino.RhinoApp.WriteLine('AHA checks started: ' + report_path)
    return report_path

if __name__ == '__main__': main()
