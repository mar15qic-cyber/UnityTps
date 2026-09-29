"""Focused regression after composing throw and scope visibility; original screenshots retained."""
import importlib.util,json,sys,time
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
for weapon in ['m4','service_pistol','sniper01','sniper02','sniper03']:
    c.equip(weapon,connection)
    for role in ['server','alpha','bravo']: c.cmd(role,'attachment',target=connection,name='')
    for aim in [False,True]: c.fire_case(weapon+'-final-bare-'+('ads' if aim else 'hip'),connection,aim)
    if weapon=='m4':
        for role in ['server','alpha','bravo']: c.cmd(role,'attachment',target=connection,name='attach.rifle.muzzle')
        for aim in [False,True]: c.fire_case(weapon+'-final-suppressor-'+('ads' if aim else 'hip'),connection,aim)
for role in ['alpha','bravo']: c.cmd(role,'map-visual')
c.cmd('alpha','stop')
(c.run/'final-smoke-result.json').write_text(json.dumps(dict(completed=True,cases=c.cases),indent=2))
print('FINAL_SMOKE_COMPLETE',flush=True)
