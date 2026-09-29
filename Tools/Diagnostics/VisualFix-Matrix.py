"""Screenshot matrix for the explicitly authorized visual-fix campaign."""
import importlib.util, json, sys, time
from pathlib import Path
spec=importlib.util.spec_from_file_location('capture',Path(__file__).with_name('VisualFix-Capture.py'))
c=importlib.util.module_from_spec(spec);spec.loader.exec_module(c)
snapshots=c.wait_ready()
owner=next(p for p in snapshots[0]['players'] if p['owner']); connection=owner['connection']
other=next(p for p in snapshots[0]['players'] if not p['owner'])['connection']
for role in ['server','alpha','bravo']:
    c.cmd(role,'pose',target=connection,position=dict(x=-31.5,y=0,z=0),yaw=90,pitch=0)
    c.cmd(role,'pose',target=other,position=dict(x=-29.5,y=0,z=-3),yaw=-34,pitch=7)
time.sleep(1.5)
for role in ['alpha','bravo']:
    c.cmd(role,'resolution',slot=1280)
    c.cmd(role,'stop',fps=60)
for weapon in (c.weapons if (len(sys.argv)<3 or sys.argv[2]=='guns') else []):
    c.equip(weapon,connection)
    for role in ['server','alpha','bravo']: c.cmd(role,'attachment',target=connection,name='')
    for aim in [False,True]: c.fire_case(weapon+'-bare-'+('ads' if aim else 'hip'),connection,aim)
    if not weapon.startswith('sniper'):
        attachment='attach.pistol.muzzle' if weapon in ['service_pistol','handgun02','handgun03','handgun04'] else 'attach.rifle.muzzle'
        for role in ['server','alpha','bravo']: c.cmd(role,'attachment',target=connection,name=attachment)
        for aim in [False,True]: c.fire_case(weapon+'-suppressor-'+('ads' if aim else 'hip'),connection,aim)
if len(sys.argv)<3 or sys.argv[2]=='guns':
    for weapon in ['m4','service_pistol','sniper02']:
        c.equip(weapon,connection)
        for role in ['server','alpha','bravo']: c.cmd(role,'attachment',target=connection,name='')
        name=weapon+'-jump-strafe-fire'
        c.cmd('server','reset',target=connection)
        c.cmd('alpha','reset')
        c.cmd('alpha','visual',caseId=name,duration=2.5)
        c.cmd('alpha','input',fire=True,jump=True,move=dict(x=-.7,y=0),look=dict(x=1,y=.3),duration=1,clickInterval=.25,caseId=name)
        time.sleep(.25)
        c.cmd('alpha','screenshot',name=name+'-airborne',caseId=name)
        time.sleep(.4)
        c.cmd('alpha','screenshot',name=name+'-landing',caseId=name)
        time.sleep(1.5)
        c.cases.append(name)
        print('CASE '+name,flush=True)
        for role in ['server','alpha','bravo']: c.cmd(role,'pose',target=connection,position=dict(x=-31.5,y=0,z=0),yaw=90,pitch=0)
        time.sleep(1)
for weapon in (c.weapons if (len(sys.argv)<3 or sys.argv[2]=='throws') else []):
    c.equip(weapon,connection)
    for kind in ['frag','flash','smoke']:
        name=weapon+'-'+kind+'-throw'
        c.cmd('server','reset',target=connection)
        if kind=='frag': c.cmd('server','throw-reset',target=connection)
        c.cmd('alpha','stop',caseId=name)
        time.sleep(.35)
        c.cmd('alpha','select-throw',caseId=name)
        time.sleep(.35)
        c.cmd('alpha','visual',caseId=name,duration=3.0)
        time.sleep(.15)
        c.cmd('alpha','throw',caseId=name)
        time.sleep(.55)
        if weapon in ['m4','service_pistol','sniper03']: c.cmd('bravo','screenshot',name='observer-'+name)
        time.sleep(2.8)
        c.cases.append(name)
        (c.run/'cases-progress.json').write_text(json.dumps(c.cases,indent=2))
        print('CASE '+name,flush=True)
(c.run/'matrix-result.json').write_text(json.dumps(dict(completed=True,cases=c.cases),indent=2))
print('MATRIX_COMPLETE',flush=True)



