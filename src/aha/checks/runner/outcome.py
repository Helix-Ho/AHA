"""Completion status shared by the terminal and Rhino runners."""
def outcome(cases):
    statuses = [c['status'] for c in cases]
    if not statuses or any(s not in ('passed', 'failed', 'unavailable') for s in statuses):
        return 'failed', 1
    if 'failed' in statuses:
        return 'failed', 1
    if 'unavailable' in statuses:
        return 'unavailable', 2
    return 'passed', 0


def host_result(result):
    """Accept only a completed report with actual assertions, never a start marker."""
    if not isinstance(result, dict):
        return 'failed'
    status = result.get('status')
    if status == 'unavailable':
        return 'unavailable'
    if status in ('failed', 'completed_with_failures') or result.get('success') is False:
        return 'failed'
    if status not in (None, 'passed') or (status is None and result.get('success') is not True):
        return 'failed'
    cases = result.get('cases')
    if isinstance(cases, list) and cases:
        return 'passed' if all(isinstance(case, dict) and
            (case.get('status') == 'passed' or (case.get('status') is None and case.get('success') is True))
            and case.get('success') is not False for case in cases) else 'failed'
    assertions = result.get('assertions')
    return 'passed' if isinstance(assertions, int) and not isinstance(assertions, bool) and assertions > 0 else 'failed'
