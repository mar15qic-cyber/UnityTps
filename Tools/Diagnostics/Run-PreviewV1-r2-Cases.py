"""One bounded campaign against the r2 test copies; never starts another game process."""
import json, os, time, sys
from pathlib import Path

root = Path(__file__).resolve().parents[2]
run = root / 'Logs/PreviewV1-r2/runtime-round1'
weapons = ['m4','ak','rifle03','service_pistol','handgun02','handgun03','handgun04',
           'smg01','smg02','smg03','smg04','smg05','shotgun01','sniper01','sniper02','sniper03']
seq = int(time.time())
cases = []

def state(role):
    try: return json.loads((run / (role+'.state.json')).read_text(encoding='utf-8-sig'))
    except (OSError, ValueError): return {}

def cmd(role, op, **args):
    global seq
    seq += 1
    value = dict(seq=seq, op=op, **args)
    path = run / (role+'.command.json')
    temp = path.with_suffix('.tmp')
    temp.write_text(json.dumps(value), encoding='utf-8')
    for _ in range(30):
        try: os.replace(temp, path); break
        except PermissionError: time.sleep(.03)
    else: raise RuntimeError('Cannot submit command: '+role)
    deadline = time.monotonic()+8
    while time.monotonic()<deadline:
        if state(role).get('seq',0)>=seq: return
        time.sleep(.06)
    raise RuntimeError('Command not acknowledged: '+role+' '+op)

def wait_ready():
    deadline=time.monotonic()+150
    while time.monotonic()<deadline:
        snapshots=[state(r) for r in ['alpha','bravo','server']]
        if all(s.get('phase')=='InProgress' and len(s.get('players',[]))==2 for s in snapshots): return snapshots
        time.sleep(1)
    raise RuntimeError('Match did not reach InProgress: '+str([(s.get('scene'),s.get('phase'),s.get('uiStatus')) for s in snapshots]))

def equip(weapon, connection):
    for role in ['server','alpha','bravo']:
        cmd(role,'equip',target=connection,weapon='weapon.'+weapon,caseId=weapon+'-equip')
    time.sleep(1)

def fire_case(name, connection, aim):
    cmd('server','reset',target=connection,caseId=name)
    cmd('alpha','reset',caseId=name)
    cmd('alpha','input',aim=aim,duration=.6,fps=60,caseId=name)
    time.sleep(.5)
    cmd('alpha','visual',caseId=name,duration=1.8)
    cmd('alpha','input',fire=True,aim=aim,clickInterval=.25,duration=1.0,caseId=name)
    time.sleep(2)
    cases.append(name)
    (run/'cases-progress.json').write_text(json.dumps(cases,indent=2))
    print('CASE '+name,flush=True)

if (run/'campaign-started.json').exists():
    previous=json.loads((run/'campaign-result.json').read_text())
    if '--resume-setup' not in sys.argv or previous.get('cases') or (run/'cases-progress.json').exists():
        raise RuntimeError('This campaign was already started; no second pass.')
    (run/'campaign-setup-failure.json').write_text(json.dumps(previous,indent=2))
else:
    (run/'campaign-started.json').write_text(json.dumps(dict(startedUtc=time.time())))
try:
    snapshots=wait_ready()
    owner=next(p for p in snapshots[0]['players'] if p['owner'])
    connection=owner['connection']
    for role in ['alpha','bravo']:
        cmd(role,'stop',fps=60)
        cmd(role,'map-visual')
        cmd(role,'screenshot',name='nightrelay-in-match')
    for weapon in weapons:
        equip(weapon,connection)
        fire_case(weapon+'-bare-hip',connection,False)
        fire_case(weapon+'-bare-ads',connection,True)
        if not weapon.startswith('sniper'):
            attachment='attach.pistol.muzzle' if weapon in ['service_pistol','handgun02','handgun03','handgun04'] else 'attach.rifle.muzzle'
            for role in ['server','alpha','bravo']:
                cmd(role,'attachment',target=connection,name=attachment,caseId=weapon+'-suppressor')
            time.sleep(.3)
            fire_case(weapon+'-suppressor-hip',connection,False)
            fire_case(weapon+'-suppressor-ads',connection,True)
    for weapon in weapons:
        equip(weapon,connection)
        cmd('server','throw-reset',target=connection,caseId=weapon+'-throw')
        cmd('alpha','stop',caseId=weapon+'-throw')
        time.sleep(.3)
        cmd('alpha','select-throw',caseId=weapon+'-throw')
        time.sleep(.4)
        cmd('alpha','visual',caseId=weapon+'-throw',duration=2.2)
        time.sleep(.24)
        cmd('alpha','throw',caseId=weapon+'-throw')
        time.sleep(2.35)
        cases.append(weapon+'-throw')
        (run/'cases-progress.json').write_text(json.dumps(cases,indent=2))
        print('CASE '+weapon+'-throw',flush=True)
    result=dict(completed=True,cases=cases,finishedUtc=time.time())
except Exception as error:
    result=dict(completed=False,cases=cases,error=str(error),finishedUtc=time.time())
finally:
    for role in ['alpha','bravo','server']:
        try: cmd(role,'stop',caseId='round1-end')
        except Exception: pass
    (run/'campaign-result.json').write_text(json.dumps(result,indent=2))
    print(json.dumps(result),flush=True)
