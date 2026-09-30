import sys,time,json,shutil
from pathlib import Path
from realtest0930_commands import read,send

run=Path(sys.argv[1]); results=[]
def owner(role='alpha'):
    return next(p for p in read(run/f'{role}.state.json')['players'] if p['owner'])
def check(name,ok,**data):
    results.append(dict(case=name,passed=bool(ok),**data))
    (run/'ui-kr-results.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
    print(name,bool(ok),flush=True)
def ui(name,wait=.7):return send(run,'alpha',wait=wait,op='ui',name=name)
def keys(*names):
    for n in names:send(run,'alpha',op='key',name=n)
def capture(name):send(run,'alpha',op='screenshot',name=name,wait=.3)
def listui(name):
    send(run,'alpha',op='ui-list');s=read(run/'alpha.ui.json')
    shutil.copyfile(run/'alpha.ui.json',run/f'ui-{name}.json');return s
deadline=time.monotonic()+120
while time.monotonic()<deadline:
    if all((run/f'{r}.state.json').exists() for r in ['alpha','bravo','server']) and all(len(read(run/f'{r}.state.json')['players'])==2 for r in ['alpha','bravo','server']):break
    time.sleep(1)
else:raise RuntimeError('Two-client match did not become ready')
for r in ['alpha','bravo','server']:send(run,r,op='stop',fps=60)
cid=owner()['connection'];spawn=owner()['position'].copy()
time.sleep(3)
check('owner-manifest-15',len(owner()['backpackManifest'])==15,manifest=owner()['backpackManifest'])
keys('B');s=listui('backpack-initial')
check('match-event-system',s['eventSystem'])
ui('BackpackTab2');p=owner()
check('mouse-switch-2',p['activeBackpack']==1,items=p['throwableItems'])
keys('B','Digit3');p=owner()
check('numeric-switch-3',p['activeBackpack']==2,items=p['throwableItems'])
for width in [1280,1920,2560]:
    send(run,'alpha',wait=.6,op='resolution',slot=width)
    keys('B');capture('backpack-'+str(width));keys('Escape')
send(run,'alpha',op='resolution',slot=1280)
keys('Enter','B');s=listui('chat-blocks-backpack')
check('chat-blocks-backpack',not any(b['name']=='BackpackTab1' for b in s['buttons']))
keys('Escape','Escape','B');s=listui('menu-blocks-backpack')
check('menu-blocks-backpack',not any(b['name']=='BackpackTab1' for b in s['buttons']))
keys('Escape')
outside=dict(spawn);outside['x']+=18
send(run,'server',op='pose',target=cid,position=outside)
send(run,'alpha',op='pose',position=outside)
time.sleep(.8)
p=owner();check('killrace-leave-lock',p['backpackEligibility']=='LockedByLeaving',reason=p['backpackEligibility'])
send(run,'server',op='pose',target=cid,position=spawn)
send(run,'alpha',op='pose',position=spawn)
time.sleep(.8)
p=owner();check('killrace-return-stays-locked',p['backpackEligibility']=='LockedByLeaving',reason=p['backpackEligibility'])
keys('B');capture('killrace-denial')
send(run,'server',op='kill-fixture',target=cid)
time.sleep(6)
p=owner();check('killrace-respawn-restores',p['backpackEligibility']=='None',reason=p['backpackEligibility'])
send(run,'alpha',op='input',fire=True,duration=.06,wait=.6)
p=owner();check('killrace-fire-lock',p['backpackEligibility']=='LockedByFire',reason=p['backpackEligibility'])
capture('killrace-fire-lock')
send(run,'server',op='kill-fixture',target=cid);time.sleep(6)
# Each fixture uses the actual built FP/TP views and normal attachment calibration.
for weapon in ['pistol.day2','handgun.02','handgun.03','handgun.04']:
    for r in ['server','alpha','bravo']:send(run,r,op='equip',target=cid,weapon=weapon)
    for optic in ['attach.rifle.optic','attach.lpfp.optic.02']:
        for r in ['server','alpha','bravo']:send(run,r,op='attachment',target=cid,name=optic)
        send(run,'alpha',op='input',aim=True,duration=15)
        stem=weapon+'-'+optic
        send(run,'alpha',op='visual',caseId=stem,duration=.15)
        send(run,'bravo',op='visual',caseId='tp-'+stem,duration=.15)
        capture(stem)
        check(stem,owner()['weapon']==weapon)
    for r in ['server','alpha','bravo']:send(run,r,op='attachment',target=cid,name='')
send(run,'server',op='end-match',wait=7)
keys('Escape')
s=listui('lobby');check('lobby-event-system',s['eventSystem'])
print('LOBBY_READY_FOR_MANUAL_UI',flush=True)
