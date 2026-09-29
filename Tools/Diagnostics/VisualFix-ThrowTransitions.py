"""ADS-to-throw and moving throw boundaries, on the already running test pair."""
import importlib.util, json, sys, time
from pathlib import Path
spec=importlib.util.spec_from_file_location('capture',Path(__file__).with_name('VisualFix-Capture.py'))
c=importlib.util.module_from_spec(spec);spec.loader.exec_module(c)
c.wait_ready()
for role in ['alpha','bravo']:
    c.cmd(role,'resolution',slot=1280)
    c.cmd(role,'stop',fps=60)
connection=next(p for p in c.state('alpha')['players'] if p['owner'])['connection']
other=next(p for p in c.state('alpha')['players'] if not p['owner'])['connection']
for role in ['server','alpha','bravo']:
    c.cmd(role,'pose',target=connection,position=dict(x=-31.5,y=0,z=0),yaw=90,pitch=0)
    c.cmd(role,'pose',target=other,position=dict(x=-29.5,y=0,z=-3),yaw=-34,pitch=7)
for weapon, moving in [('m4',False),('service_pistol',False),('sniper01',False),('sniper02',False),('sniper03',False),('m4',True)]:
    name=weapon+('-moving' if moving else '-ads')+'-transition-throw'
    c.equip(weapon,connection)
    c.cmd('server','throw-reset',target=connection)
    c.cmd('alpha','input',aim=not moving,move=dict(x=.4 if moving else 0,y=0),duration=8)
    time.sleep(.8)
    c.cmd('alpha','select-throw',caseId=name)
    time.sleep(.5)
    c.cmd('alpha','visual',caseId=name,duration=3)
    time.sleep(.15)
    c.cmd('alpha','throw',caseId=name)
    time.sleep(3.2)
    c.cmd('alpha','stop')
    time.sleep(.5)
    c.cmd('alpha','screenshot',name=name+'-restored')
    c.cases.append(name)
    print('CASE '+name,flush=True)
(c.run/'throw-transitions-result.json').write_text(json.dumps(dict(completed=True,cases=c.cases),indent=2))
print('THROW_TRANSITIONS_COMPLETE',flush=True)
