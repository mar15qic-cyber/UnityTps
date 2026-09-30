import importlib.util,sys,time
from pathlib import Path
spec=importlib.util.spec_from_file_location('capture',Path(__file__).with_name('VisualFix-Capture.py'))
c=importlib.util.module_from_spec(spec);spec.loader.exec_module(c)
s=c.state('alpha'); connection=next(p for p in s['players'] if p['owner'])['connection']; other=next(p for p in s['players'] if not p['owner'])['connection']
for role in ['server','alpha','bravo']:
    c.cmd(role,'pose',target=other,position=dict(x=-31.5,y=0,z=0),yaw=90,pitch=0)
    c.cmd(role,'pose',target=connection,position=dict(x=-31.5,y=0,z=-12.5),yaw=90,pitch=0)
time.sleep(1.5)
c.equip('m4',connection)
c.cmd('server','throw-reset',target=connection)
c.cmd('alpha','select-throw',caseId='ramp-clear-throw')
time.sleep(.4)
c.cmd('alpha','visual',caseId='ramp-clear-throw',duration=3.5)
time.sleep(.24)
c.cmd('alpha','throw',caseId='ramp-clear-throw')
time.sleep(4)
print('RAMP_CAPTURE_COMPLETE',flush=True)
