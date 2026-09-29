import collections,json,sys
from pathlib import Path
p=Path(__file__).resolve().parents[2]/'Logs/VisualFix0928'/sys.argv[1]
rows=list(map(json.loads,(p/'alpha.visual.jsonl').read_text().splitlines()))
cases=collections.defaultdict(list)
for r in rows: cases[r['caseId']].append(r)
transitions=json.loads((p/'throw-transitions-result.json').read_text())
shots=json.loads((p/'final-smoke-result.json').read_text())
assert transitions['completed'] and shots['completed']
failures=[]
for name in transitions['cases']:
    samples=cases[name]
    windup=[r for r in samples if 0<=r['throwElapsed']<.34]
    active=[r for r in samples if 0<=r['throwElapsed']<1.05]
    if not windup or any(not r['heldVisible'] for r in windup): failures.append(name+': held object outside viewport')
    if not active or any(r['weaponVisible'] for r in active): failures.append(name+': weapon visible during throw')
    if not any(r['phase']=='Released' for r in samples): failures.append(name+': no release observed')
for name in shots['cases']:
    active=[r for r in cases[name] if r['traces']>0]
    if not active: failures.append(name+': no tracer')
    if any(r['wrongTracerLayers'] or r['tracerPixelError']>.25 for r in active): failures.append(name+': tracer camera/exit mismatch')
for role in ['alpha','bravo']:
    result=json.loads((p/(role+'.map-visual.json')).read_text())
    if result['invalid']: failures.append(role+': invalid map shader')
result=dict(passed=not failures,throwCases=len(transitions['cases']),fireCases=len(shots['cases']),failures=failures)
(p/'final-verification.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result))
sys.exit(1 if failures else 0)
