"""One bounded campaign against the r2 test copies; never starts another game process."""
import json, os, time, sys
from pathlib import Path

root = Path(__file__).resolve().parents[2]
run = root / 'Logs/VisualFix0928' / sys.argv[1]
weapons = ['m4','ak','rifle03','service_pistol','handgun02','handgun03','handgun04',
           'smg01','smg02','smg03','smg04','smg05','shotgun01','sniper01','sniper02','sniper03']
seq = int(time.time())
for role in ['server','alpha','bravo']:
    try:
        seq = max(seq, int(json.loads((run / (role+'.state.json')).read_text(encoding='utf-8-sig')).get('seq', 0)))
    except (OSError, ValueError):
        pass
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
        cmd(role,'equip',target=connection,weapon='weapon.'+weapon,slots=['weapon.'+weapon],caseId=weapon+'-equip')
    time.sleep(1)

def fire_case(name, connection, aim):
    cmd('server','reset',target=connection,caseId=name)
    cmd('alpha','reset',caseId=name)
    cmd('alpha','input',aim=aim,duration=4,fps=60,caseId=name)
    deadline=time.monotonic()+3
    while time.monotonic()<deadline:
        owner=next((p for p in state('alpha').get('players',[]) if p['owner']),{})
        blend=owner.get('ads',0)
        if (aim and blend>=.98) or (not aim and blend<=.02): break
        time.sleep(.06)
    else: raise RuntimeError('Aim pose did not settle: '+name)
    time.sleep(.15)
    cmd('alpha','visual',caseId=name,duration=1.8)
    cmd('alpha','input',fire=True,aim=aim,clickInterval=.25,duration=1.0,caseId=name)
    time.sleep(2)
    cases.append(name)
    (run/'cases-progress.json').write_text(json.dumps(cases,indent=2))
    print('CASE '+name,flush=True)

if __name__ == '__main__':
    snapshots=wait_ready()
    connection=next(p for p in snapshots[0]['players'] if p['owner'])['connection']
    for role in ['alpha','bravo']:
        cmd(role,'stop',fps=60)
        cmd(role,'resolution',slot=1280)
    for weapon in ['m4','service_pistol']:
        equip(weapon,connection)
        for role in ['server','alpha','bravo']: cmd(role,'attachment',target=connection,name='')
        fire_case(weapon+'-bare-hip',connection,False)
        fire_case(weapon+'-bare-ads',connection,True)
        attachment='attach.pistol.muzzle' if weapon=='service_pistol' else 'attach.rifle.muzzle'
        for role in ['server','alpha','bravo']: cmd(role,'attachment',target=connection,name=attachment)
        fire_case(weapon+'-suppressor-hip',connection,False)
    equip('m4',connection)
    cmd('server','throw-reset',target=connection,caseId='m4-throw')
    cmd('alpha','stop',caseId='m4-throw')
    time.sleep(.5)
    cmd('alpha','select-throw',caseId='m4-throw')
    time.sleep(.4)
    cmd('alpha','visual',caseId='m4-throw',duration=3.5)
    time.sleep(.24)
    cmd('alpha','throw',caseId='m4-throw')
    time.sleep(4)
    cmd('alpha','stop')
    # Reproduce the exact r2 spawn/heading where the giant released grenade was captured.
    other=next(p for p in state('alpha')['players'] if not p['owner'])['connection']
    for role in ['server','alpha','bravo']:
        cmd(role,'pose',target=other,position=dict(x=-31.5,y=0,z=0),yaw=90,pitch=0)
        cmd(role,'pose',target=connection,position=dict(x=-31.5,y=0,z=-12.5),yaw=90,pitch=0,caseId='r2-ramp-repro')
    time.sleep(1)
    cmd('server','throw-reset',target=connection)
    cmd('alpha','select-throw',caseId='ramp-throw')
    time.sleep(.4)
    cmd('alpha','visual',caseId='ramp-throw',duration=3.5)
    time.sleep(.24)
    cmd('alpha','throw',caseId='ramp-throw')
    time.sleep(4)
    cmd('alpha','stop')
    print('FOCUSED_CAPTURE_COMPLETE',flush=True)




