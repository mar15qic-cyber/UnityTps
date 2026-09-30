"""Capture the final built shotgun view during the remaining sleeve motions."""
import json, sys, time
from pathlib import Path
from realtest0930_commands import read, send

r = Path(sys.argv[1])
results = []
def owner():
    return next(p for p in read(r/'alpha.state.json')['players'] if p['owner'])
def command(**values):
    return send(r, 'alpha', **values)
def capture(name):
    command(op='screenshot', name=name, wait=.15)
def check(name, ok, **evidence):
    results.append(dict(case=name, passed=bool(ok), **evidence))
    (r/'arms-motion-results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print(name, bool(ok), flush=True)

deadline = time.monotonic()+150
while time.monotonic() < deadline:
    if all((r/f'{role}.state.json').exists() for role in ['alpha','bravo','server']) and all(len(read(r/f'{role}.state.json')['players']) == 2 for role in ['alpha','bravo','server']):
        break
    time.sleep(1)
else:
    raise RuntimeError('Two-client arms scene timeout')
for role in ['alpha','bravo','server']:
    send(r, role, op='stop', fps=60)
cid = owner()['connection']
for role in ['server','alpha','bravo']:
    send(r, role, op='equip', target=cid, weapon='shotgun.01', slots=['shotgun.01','pistol.day2'])
command(op='resolution', slot=1280)
time.sleep(4)
capture('shotgun-idle')
command(op='input', aim=True, duration=2, wait=.8)
capture('shotgun-ads')
check('shotgun-ads', owner()['ads'] > .9, ads=owner()['ads'])
command(op='stop')
command(op='input', move={'x':.4,'y':1}, sprint=True, duration=1.5, wait=.35)
capture('shotgun-sprint')
check('shotgun-sprint', owner()['speed'] > .5, speed=owner()['speed'])
command(op='stop')
before = read(r/'alpha.state.json')['accepted']
command(op='input', fire=True, duration=.06, wait=.02)
capture('shotgun-recoil')
time.sleep(.8)
check('shotgun-recoil', read(r/'alpha.state.json')['accepted'] > before)
command(op='stop')
counts = owner()['throwableCounts']
command(op='select-throw', wait=.8)
capture('shotgun-throw-selected')
command(op='visual', caseId='shotgun-throw-recovery', duration=4, wait=.01)
command(op='throw', wait=.15)
capture('shotgun-throw')
time.sleep(3.8)
capture('shotgun-throw-restored')
p = owner()
check('shotgun-throw-restored', p['weapon']=='shotgun.01' and p['action']=='None' and sum(p['throwableCounts']) < sum(counts), weapon=p['weapon'], action=p['action'], counts=p['throwableCounts'])
samples=[json.loads(line) for line in (r/'alpha.visual.jsonl').read_text(encoding='utf-8-sig').splitlines()]
check('shotgun-held-model-and-recovery', any(x['heldVisible'] for x in samples) and any(x['throwElapsed']>=0 for x in samples) and samples[-1]['weaponVisible'] and not samples[-1]['throwing'])
if not all(c['passed'] for c in results):
    sys.exit(2)
