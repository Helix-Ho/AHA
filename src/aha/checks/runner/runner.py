"""Shared deterministic check catalogue and terminal runner (standard library only)."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time
import uuid
from outcome import outcome
from catalogue import catalogue as read_catalogue, select, validate_local_fixture_set, TOOLS

ROOT = Path(__file__).resolve().parents[4]
def catalogue(root=ROOT):
    return read_catalogue(root)

def environment():
    env = os.environ.copy()
    configured = os.environ.get('AHA_DOTNET')
    dotnet = (shutil.which(configured) or configured) if configured else str(ROOT / '.tools/dotnet/dotnet')
    if not configured and not Path(dotnet).is_file():
        dotnet = shutil.which('dotnet') or dotnet
    env['DOTNET_CLI_HOME'] = str(ROOT / '.tools/dotnet-home')
    env['NUGET_PACKAGES'] = str(ROOT / '.tools/nuget')
    env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    return dotnet, env


def run_process(command, log, env, timeout):
    started = time.monotonic()
    try:
        with log.open('w') as stream:
            proc = subprocess.Popen(command, cwd=ROOT, env=env, stdout=stream, stderr=subprocess.STDOUT,
                                    start_new_session=os.name == 'posix')
            try:
                proc.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                # dotnet/Python checks can have child processes. Stop their group as well.
                if os.name == 'posix':
                    try: os.killpg(proc.pid, signal.SIGKILL)
                    except ProcessLookupError: pass
                else:
                    proc.kill()
                proc.wait()
                return {'status': 'failed', 'error': 'Check timed out', 'log': str(log)}
        return {'status': 'passed' if proc.returncode == 0 else 'failed', 'exit_code': proc.returncode,
                'elapsed_seconds': round(time.monotonic() - started, 3), 'log': str(log)}
    except OSError as error:
        return {'status': 'unavailable', 'error': str(error)}


def claim_run(path):
    """Do not let overlapping invocations build the same projects or overwrite logs."""
    for attempt in range(2):
        try:
            descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(descriptor, 'w') as stream:
                json.dump({'pid': os.getpid()}, stream)
            return
        except FileExistsError:
            try:
                pid = json.loads(path.read_text())['pid']
                if os.name == 'posix':
                    os.kill(pid, 0)
            except ProcessLookupError:
                if attempt == 0:
                    path.unlink()
                    continue
            except (ValueError, KeyError, OSError):
                pass
            raise RuntimeError('Another check runner owns ' + str(path))


def main(default_tool='all', argv=None):
    parser = argparse.ArgumentParser(description='AHA core regression checks. No KEY, external API or AI required.')
    parser.add_argument('--tool', choices=('all',) + TOOLS, default=default_tool)
    parser.add_argument('--layer', choices=('offline', 'rhino', 'all'), default='offline')
    parser.add_argument('--list', action='store_true')
    parser.add_argument('--build-rhino', action='store_true', help='Build selected Rhino suites; does not report host checks as passed.')
    parser.add_argument('--include-local-fixtures', action='store_true', help='Also run recorded fixtures stored under .local/test-data.')
    args = parser.parse_args(argv)
    output = ROOT / 'outputs/checks'
    output.mkdir(parents=True, exist_ok=True)
    target = output / ('build.json' if args.build_rhino else 'offline.json' if args.layer == 'offline' else 'terminal.json')
    run_id = uuid.uuid4().hex
    lock = output / 'terminal.lock'
    claimed = False
    try:
        if not args.list:
            try:
                claim_run(lock)
                claimed = True
            except RuntimeError as error:
                rejected = output / ('rejected-' + run_id + '.json')
                rejected.write_text(json.dumps({'status': 'unavailable', 'exit_code': 2, 'run_id': run_id, 'cases': [], 'error': str(error)}))
                print(str(rejected), file=sys.stderr)
                return 2
            target.write_text(json.dumps({'status': 'running', 'run_id': run_id, 'cases': []}))
        entries = catalogue()
        suites = select(entries, args.tool, 'rhino' if args.build_rhino else args.layer, args.include_local_fixtures)
        if args.build_rhino and not any(s['id'] == 'runtime' for s in suites):
            suites.insert(0, next(s for s in entries if s['id'] == 'runtime'))  # host build-identity reader
        if args.list:
            for suite in suites: print(suite['id'], suite['layer'], suite['path'])
            return 0
        dotnet, env = environment()
        cases = []
        if args.build_rhino:
            result = run_process([dotnet, 'build', 'src/aha/rhino/Aha.csproj', '-c', 'Release', '--nologo'], output/'product-build.log', env, 180)
            if result['status'] != 'passed':
                status, code = outcome([result])
                target.write_text(json.dumps({'status': status, 'exit_code': code, 'run_id': run_id, 'operation': 'build', 'cases': [dict(result, id='product')]}))
                print(json.dumps(result)); return code
        for suite in suites:
            if suite.get('scope') == 'recorded':
                try:
                    validate_local_fixture_set(str(ROOT), suite['tool'])
                except IOError as error:
                    result = {'status': 'unavailable', 'error': str(error)}
                    cases.append(dict(result, id=suite['id'], tool=suite['tool'], layer=suite['layer'], scope='recorded'))
                    print(suite['id'] + ': unavailable', flush=True)
                    continue
            if suite['layer'] == 'rhino' and not args.build_rhino:
                result = {'status': 'unavailable', 'error': 'Run src/aha/checks/run_rhino.py on the Rhino UI thread.'}
            else:
                suite_env = env.copy()
                suite_env['AHA_CHECK_DATA_SCOPE'] = suite.get('scope', 'public')
                if suite.get('scope') == 'recorded':
                    suite_env['AHA_CHECK_LOCAL_FIXTURES'] = str(ROOT / '.local/test-data')
                kind = suite['kind']
                if kind in ('dotnet', 'rhino'):
                    command = [dotnet, 'build'] if args.build_rhino else [dotnet, 'run', '--project']
                    command += [suite['path'], '-c', 'Release', '--nologo']
                    if not args.build_rhino and suite.get('args'): command += ['--'] + suite['args']
                elif kind == 'node': command = [shutil.which('node') or 'node', suite['path']]
                else: command = [str(ROOT/'.venv/bin/python'), suite['path']]
                if kind in ('node', 'python'): command += suite.get('args', [])
                result = run_process(command, output/(suite['id'] + '.log'), suite_env, suite.get('timeout_seconds', 300))
            cases.append(dict(result, id=suite['id'], tool=suite['tool'], layer=suite['layer'], scope=suite.get('scope', 'public')))
            print(suite['id'] + ': ' + result['status'], flush=True)
        status, code = outcome(cases)
        report = {'status': status, 'exit_code': code, 'run_id': run_id, 'include_local_fixtures': args.include_local_fixtures,
                  'operation': 'build' if args.build_rhino else 'check', 'cases': cases}
        target.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n')
        print(str(target))
        return code
    except (ValueError, KeyError, OSError) as error:
        if not args.list:
            target.write_text(json.dumps({'status':'unavailable', 'exit_code':2, 'run_id':run_id, 'operation':'build' if args.build_rhino else 'check', 'cases':[], 'error':str(error)}))
        print('Check setup unavailable: ' + str(error), file=sys.stderr)
        return 2
    finally:
        if claimed:
            lock.unlink()
