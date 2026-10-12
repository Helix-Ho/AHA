"""Check ownership and entry registration shared by CPython and Rhino Python 2/3."""
import fnmatch
import hashlib
import json
import math
import os

TOOLS = ('aha', 'setting', 'info', 'eum-crawl', 'topo')
try:
    string_types = (basestring,)
except NameError:
    string_types = (str,)


def inside(path, directory):
    path = os.path.normcase(os.path.realpath(path))
    directory = os.path.normcase(os.path.realpath(directory))
    return path.startswith(directory + os.sep)


def entry_file(name):
    # Helpers have ordinary names; executable Python/Node checks use these names.
    return name.endswith('.csproj') or any(fnmatch.fnmatch(name, pattern) for pattern in (
        'test_*.py', 'check_*.py', 'test_*.js', 'check_*.js', '*-checks.js',
        'test_*.cjs', 'check_*.cjs', '*-checks.cjs', 'test_*.mjs', 'check_*.mjs', '*-checks.mjs'))


def catalogue(root):
    root = os.path.abspath(str(root))
    suites, ids, paths, actual = [], set(), set(), set()
    for tool in TOOLS:
        directory = os.path.join(root, 'src', tool, 'checks')
        file = os.path.join(directory, 'suites.json')
        with open(file) as stream:
            entries = json.load(stream)
        if not isinstance(entries, list) or not entries:
            raise ValueError('Empty or invalid catalogue: ' + file)
        for entry in entries:
            if not isinstance(entry, dict):
                raise ValueError('Check entry must be an object: ' + file)
            check_id = entry.get('id')
            if not isinstance(check_id, string_types) or not check_id or any(c not in 'abcdefghijklmnopqrstuvwxyz0123456789-' for c in check_id):
                raise ValueError('Invalid check id: ' + str(check_id))
            if check_id in ids:
                raise ValueError('Duplicate check id: ' + check_id)
            ids.add(check_id)
            kind, layer = entry.get('kind'), entry.get('layer')
            if 'scope' in entry:
                raise ValueError('Check scope is selected at execution, not registered: ' + check_id)
            if not isinstance(entry.get('local_fixtures', False), bool):
                raise ValueError('Invalid local fixture capability: ' + check_id)
            local_args = entry.get('local_args', [])
            if 'local_args' in entry and (not entry.get('local_fixtures') or not isinstance(local_args, list)
                    or not all(isinstance(a, string_types) for a in local_args)):
                raise ValueError('Invalid local check arguments: ' + check_id)
            if kind not in ('dotnet', 'node', 'python', 'rhino') or layer != ('rhino' if kind == 'rhino' else 'offline'):
                raise ValueError('Invalid check kind/layer: ' + check_id)
            relative = entry.get('path')
            if not isinstance(relative, string_types) or os.path.isabs(relative):
                raise ValueError('Check path must be relative: ' + check_id)
            path = os.path.realpath(os.path.join(root, relative))
            if not inside(path, directory):
                raise ValueError('Check must belong to its owner: ' + check_id)
            suffix = os.path.splitext(path)[1]
            allowed = { 'dotnet': ('.csproj',), 'rhino': ('.csproj',), 'python': ('.py',), 'node': ('.js', '.cjs', '.mjs') }
            if suffix not in allowed[kind]:
                raise ValueError('Check path does not match its kind: ' + check_id)
            if path in paths:
                raise ValueError('Duplicate check path: ' + path)
            paths.add(path)
            if not os.path.isfile(path):
                raise IOError('Missing check file: ' + path)
            arguments = entry.get('args', [])
            if not isinstance(arguments, list) or not all(isinstance(a, string_types) for a in arguments):
                raise ValueError('Check args must be strings: ' + check_id)
            timeout = entry.get('timeout_seconds', 1800 if kind == 'rhino' else 300)
            if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or math.isnan(timeout) or math.isinf(timeout) or timeout <= 0:
                raise ValueError('Invalid check timeout: ' + check_id)
            if kind == 'rhino' and (not isinstance(entry.get('type'), string_types) or not entry['type'] or entry.get('method') not in ('Start', 'RunSafe')):
                raise ValueError('Invalid Rhino entry point: ' + check_id)
            suites.append(dict(entry, tool=tool))
        for current, directories, files in os.walk(directory):
            directories[:] = [d for d in directories if d not in ('bin', 'obj', '__pycache__')]
            actual.update(os.path.realpath(os.path.join(current, name)) for name in files if entry_file(name))
    if any(s['id'] + '-recorded' in ids for s in suites if s.get('local_fixtures')):
        raise ValueError('Recorded check id conflicts with a registered check')
    missing = actual - paths
    if missing:
        raise ValueError('Unregistered checks: ' + ', '.join(sorted(missing)))
    return suites


def select(suites, tool='all', layer='offline', include_local_fixtures=False):
    if tool not in ('all',) + TOOLS or layer not in ('offline', 'rhino', 'all'):
        raise ValueError('Unknown tool/layer')
    result = []
    for suite in suites:
        if tool not in ('all', suite['tool']) or layer not in ('all', suite['layer']):
            continue
        result.append(dict(suite, scope='public'))
        if include_local_fixtures and suite.get('local_fixtures'):
            recorded = dict(suite, id=suite['id'] + '-recorded', scope='recorded')
            if 'local_args' in suite: recorded['args'] = suite['local_args']
            result.append(recorded)
    if not result:
        raise ValueError('No checks selected')
    return result


def validate_local_fixture_set(root, tool):
    directory = os.path.join(root, '.local', 'test-data', tool)
    manifest = os.path.join(directory, 'file-sha256.json')
    try:
        with open(manifest) as stream:
            expected = json.load(stream)
        if not isinstance(expected, dict) or not expected:
            raise ValueError('Empty local fixture manifest')
        for name, digest in expected.items():
            if not isinstance(name, string_types) or os.path.basename(name) != name or not isinstance(digest, string_types):
                raise ValueError('Invalid local fixture manifest entry')
            with open(os.path.join(directory, name), 'rb') as stream:
                actual = hashlib.sha256(stream.read()).hexdigest()
            if actual != digest:
                raise ValueError('Local fixture hash differs: ' + name)
    except (IOError, OSError, ValueError, TypeError) as error:
        raise IOError('Local ' + tool + ' fixtures unavailable: ' + str(error))
    return directory
