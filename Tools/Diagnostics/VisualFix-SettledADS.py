import importlib.util,json,sys,time
from pathlib import Path
spec=importlib.util.spec_from_file_location('capture',Path(__file__).with_name('VisualFix-Capture.py'))
c=importlib.util.module_from_spec(spec);spec.loader.exec_module(c)
c.wait_ready()
connection=next(p for p in c.state('alpha')['players'] if p['owner'])['connection']
for weapon in c.weapons:
    c.equip(weapon,connection)
    for role in ['server','alpha','bravo']: c.cmd(role,'attachment',target=connection,name='')
    c.fire_case(weapon+'-bare-settled-ads',connection,True)
    if not weapon.startswith('sniper'):
        attachment='attach.pistol.muzzle' if weapon in ['service_pistol','handgun02','handgun03','handgun04'] else 'attach.rifle.muzzle'
        for role in ['server','alpha','bravo']: c.cmd(role,'attachment',target=connection,name=attachment)
        c.fire_case(weapon+'-suppressor-settled-ads',connection,True)
(c.run/'settled-ads-result.json').write_text(json.dumps(dict(completed=True,cases=c.cases),indent=2))
print('SETTLED_ADS_COMPLETE',flush=True)
