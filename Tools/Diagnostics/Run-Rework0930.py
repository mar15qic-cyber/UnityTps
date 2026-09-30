"""Observe native players through the existing input/RPC path; fixtures are logged separately."""
import json, sys, time
from pathlib import Path
from realtest0930_commands import read, send

run = Path(sys.argv[1])
mode = sys.argv[2] if len(sys.argv) > 2 else 'KillRace'
results = []
def state(role='alpha'): return read(run / (role+'.state.json'))
def owner(): return next(p for p in state()['players'] if p['owner'])
def cmd(role='alpha', **args): return send(run, role, **args)
def check(name, passed, **values):
    results.append(dict(case=name, passed=bool(passed), **values))
    (run/'rework-results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print(name, bool(passed), flush=True)
def capture(name): cmd(op='screenshot', name=name, wait=.05)
def view(name, seconds): cmd(op='rework-view', name=name, duration=seconds, wait=.02)
def frames(name):
    path=run/('alpha-'+name+'.frustum.jsonl')
    return [json.loads(s) for s in path.read_text(encoding='utf-8-sig').splitlines()]
def end_check(name, seconds, limit=0):
    time.sleep(seconds+.35)
    rows=frames(name)
    check(name, len(rows)>20 and max(r['visibleEnds'] for r in rows)<=limit,
          frames=len(rows), maximumVisibleEnds=max(r['visibleEnds'] for r in rows),
          meshVertices=sorted(set(r['vertices'] for r in rows)))
def equip(weapon='shotgun.01'):
    cid=owner()['connection']
    for role in ('server','bravo','alpha'):
        cmd(role, op='equip', target=cid, weapon=weapon, slots=[weapon,'pistol.day2'], wait=.05)
    time.sleep(1.1)
def attachment(item):
    for role in ('server','bravo','alpha'):
        cmd(role, op='attachment', target=owner()['connection'], name=item, wait=.05)
    time.sleep(.7)
def ammo(count, reserve=36):
    for role in ('server','alpha'):
        cmd(role, op='ammo', target=owner()['connection'], slot=count, health=reserve, wait=.05)
def pose(position, yaw=0):
    for role in ('server','alpha'):
        cmd(role, op='pose', target=owner()['connection'], position=position, yaw=yaw, wait=.05)
    time.sleep(.5)
def respawn():
    before=owner()['life']
    cmd('server',op='kill-fixture',target=owner()['connection'])
    deadline=time.monotonic()+10
    while time.monotonic()<deadline:
        if owner()['life']>before and not owner()['dead']: break
        time.sleep(.2)
    else: raise RuntimeError('Respawn timeout')
    time.sleep(1.5)
def watch(name, seconds, record=False):
    until=time.monotonic()+seconds
    timeline=[]; images=[]
    while time.monotonic()<until:
        p=owner()
        timeline.append(dict(utc=time.time(), **p))
        if record:
            frame=name+'-'+str(len(images)).zfill(3)
            capture(frame); images.append((time.monotonic(), 'alpha-'+frame+'.png'))
        else: time.sleep(.1)
    (run/(name+'.timeline.json')).write_text(json.dumps(timeline),encoding='utf-8')
    if images:
        lines=[]
        for i,(stamp,file) in enumerate(images):
            lines.append("file '"+file+"'")
            lines.append('duration '+str(images[i+1][0]-stamp if i+1<len(images) else .3))
        lines.append("file '"+images[-1][1]+"'")
        (run/(name+'.frames.txt')).write_text('\n'.join(lines)+'\n',encoding='utf-8')
    return timeline

deadline=time.monotonic()+180
while time.monotonic()<deadline:
    try:
        if all(len(state(role)['players'])==2 and state(role)['phase']=='InProgress' for role in ('alpha','bravo','server')): break
    except (FileNotFoundError, KeyError): pass
    time.sleep(1)
else: raise RuntimeError('Native pair startup timeout')
for role in ('alpha','bravo','server'): cmd(role,op='stop',fps=60)
for role in ('alpha','bravo'): cmd(role,op='resolution',slot=1280,wait=.5)
anchor=dict(owner()['position'])
equip()
view('eligible-reload',3)
ammo(5)
cmd(op='input',reload=True,duration=.2,wait=.1)
rows=watch('eligible-reload',3.1)
samples=frames('eligible-reload')
check('eligible-busy-preserves-availability', any(p['backpackEligibility']=='PendingShots' for p in rows)
      and any(s['hintVisible'] for s in samples), eligibility=sorted(set(p['backpackEligibility'] for p in rows)))

if mode=='KillRace':
    view('shotgun-idle',1.2); capture('shotgun-idle'); end_check('shotgun-idle',1.2)
    for scope,label in (('', 'iron'),('attach.rifle.optic','scope02'),('attach.lpfp.optic.02','scope04')):
        attachment(scope)
        cmd(op='input',aim=True,duration=10,wait=.8)
        view('ads-'+label,1.6); capture('ads-'+label); end_check('ads-'+label,1.6)
        check('ads-reached-'+label,owner()['ads']>.9,ads=owner()['ads'])
        cmd(op='stop'); time.sleep(1)
    attachment('')
    ammo(0)
    view('reload-six',7.5)
    cmd(op='input',reload=True,duration=.2,wait=.02)
    rows=watch('reload-six',7.7,record=True)
    samples=frames('reload-six')
    check('reload-six-original-arms',len(samples)>100 and max(s['visibleEnds'] for s in samples)==0,
          frames=len(samples),maximumVisibleEnds=max(s['visibleEnds'] for s in samples),
          finalAmmo=owner()['ammo'],finalReserve=owner()['reserve'])
    attachment('attach.rifle.magazine')
    for role in ('server','alpha'): cmd(role,op='reset',target=owner()['connection'],wait=.05)
    magazine=owner()['ammo']; ammo(0)
    check('extended-magazine-installed',magazine>6,capacity=magazine)
    extended_seconds=2.0+magazine*.734+.7
    view('reload-extended',extended_seconds)
    cmd(op='input',reload=True,duration=.2,wait=.02)
    rows=watch('reload-extended',extended_seconds+.2,record=True)
    samples=frames('reload-extended')
    check('reload-extended-original-arms',max(s['visibleEnds'] for s in samples)==0 and owner()['ammo']==magazine,
          frames=len(samples),maximumVisibleEnds=max(s['visibleEnds'] for s in samples),finalAmmo=owner()['ammo'])
    attachment('')
    view('recoil',1.2)
    before=state()['accepted']
    cmd(op='input',fire=True,duration=.2,clickInterval=.05,wait=.02); capture('recoil'); end_check('recoil',1.2)
    check('network-shot-confirmed',state()['accepted']>before)
    ammo(4); view('locked-reload',4.2)
    cmd(op='input',reload=True,duration=.2,wait=.02)
    rows=watch('locked-reload',4.4)
    samples=frames('locked-reload')
    check('fired-life-icon-stays-hidden', all(p['backpackEligibility']=='LockedByFire' for p in rows)
          and not any(s['hintVisible'] for s in samples), frames=len(samples))
    cmd(op='stop')
    view('movement',2.3)
    cmd(op='input',move={'x':.3,'y':1},sprint=True,duration=1.8,wait=.3)
    capture('movement'); end_check('movement',2.3); cmd(op='stop')
    cmd(op='select-throw',wait=.9)
    view('throw-recovery',4.5)
    cmd(op='throw',wait=.15);capture('throw');end_check('throw-recovery',4.5)
    capture('throw-restored')
    check('throw-restores-shotgun',owner()['weapon']=='shotgun.01' and owner()['action']=='None')
    respawn()
    anchor=dict(owner()['position'])

# Teleport fixtures use existing level ground, with authority and owner positions logged.
outside=dict(anchor); outside['x']=-anchor['x'] if abs(anchor['x'])>5 else anchor['x']+18
pose(outside)
ammo(3);view('outside-reload',4.9)
cmd(op='input',reload=True,duration=.2,wait=.02)
rows=watch('outside-reload',5.1)
samples=frames('outside-reload')
check('outside-icon-stays-hidden', not any(s['hintVisible'] for s in samples)
      and not any(p['backpackEligibility'] in ('None','PendingShots') for p in rows),
      eligibility=sorted(set(p['backpackEligibility'] for p in rows)),frames=len(samples))
pose(anchor)
time.sleep(.4)
check('return-zone-rule', owner()['backpackEligibility']==('None' if mode=='TDM' else 'LockedByLeaving'),
      eligibility=owner()['backpackEligibility'])

if mode=='TDM':
    pose(outside)
    cmd(op='input',fire=True,duration=.2,clickInterval=.05,wait=1)
    pose(anchor);ammo(3)
    view('tdm-locked-reload',4.8)
    cmd(op='input',reload=True,duration=.2,wait=.02)
    rows=watch('tdm-locked-reload',5)
    samples=frames('tdm-locked-reload')
    check('tdm-left-and-fired-remains-hidden', not any(s['hintVisible'] for s in samples)
          and all(p['backpackEligibility']=='LockedByFire' for p in rows), frames=len(samples))
else:
    equip('rifle.02')
    # Actual registered weapon ID is resolved by the fixture, no presentation transforms patched.
    attachment('attach.lpw.tactical.light')
    pose(anchor)
    wall=dict(anchor);wall['z']+=3;wall['y']+=1.5
    for role in ('server','bravo','alpha'):
        cmd(role,op='geometry',name='light-wall',position=wall,scale={'x':6,'y':3,'z':.2},wait=.05)
    time.sleep(1)
    cmd(op='shadow-fixture',name='fp-on');capture('flashlight-before')
    cmd(op='shadow-fixture',name='fp-off');capture('flashlight-after')
    view('flashlight-off',1.2);time.sleep(1.5)
    rows=frames('flashlight-off')
    check('fp-shadow-casters-zero',all(s['fpShadowCasters']==0 for s in rows),frames=len(rows))
print('RESULTS',len(results),'FAILURES',sum(not r['passed'] for r in results),flush=True)
if not all(r['passed'] for r in results):sys.exit(2)
