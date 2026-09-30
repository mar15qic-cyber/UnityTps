import sys,json,time,math
from pathlib import Path
from realtest0930_commands import read,send
r=Path(sys.argv[1]);expected=sys.argv[2];results=[]
def state(role):return read(r/f'{role}.state.json')
def owner(role='alpha'):return next(p for p in state(role)['players'] if p['owner'])
def check(name,ok,**data):
 results.append(dict(case=name,passed=bool(ok),**data))
 (r/'map-live-results.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
 print(name,bool(ok),flush=True)
deadline=time.monotonic()+150
while time.monotonic()<deadline:
 if all((r/f'{role}.state.json').exists() for role in ['alpha','bravo','server']) and all(len(state(role)['players'])==2 for role in ['alpha','bravo','server']):break
 time.sleep(1)
else:raise RuntimeError('Map two-client match timeout')
for role in ['alpha','bravo','server']:send(r,role,op='stop',fps=60)
check('three-endpoints-load',all(state(role)['scene']==expected for role in ['alpha','bravo','server']),scenes={role:state(role)['scene'] for role in ['alpha','bravo','server']})
for role in ['alpha','bravo']:send(r,role,op='map-visual')
check('materials-valid',all(not read(r/f'{role}.map-visual.json')['invalid'] for role in ['alpha','bravo']),visual={role:read(r/f'{role}.map-visual.json') for role in ['alpha','bravo']})
initial=owner()['position'];cid=owner()['connection']
send(r,'alpha',op='input',move={'x':.7,'y':.7},duration=1.5,wait=2)
send(r,'alpha',op='stop',wait=.8)
current=owner()['position'];server=next(p['position'] for p in state('server')['players'] if p['connection']==cid)
distance=math.sqrt(sum((current[k]-initial[k])**2 for k in ['x','y','z']))
error=math.sqrt(sum((current[k]-server[k])**2 for k in ['x','y','z']))
check('move-and-reconcile',distance>.3 and error<1,initial=initial,position=current,serverPosition=server,distance=distance,error=error)
before=state('alpha')['accepted'];shots=state('server')['serverShots']
time.sleep(4) # Allow the actual client's initial clock synchronization to settle.
for attempt in range(3):
 send(r,'alpha',op='input',fire=True,duration=.15,wait=1)
 if state('alpha')['accepted']>before and state('server')['serverShots']>shots:break
 time.sleep(2)
check('accepted-live-fire',state('alpha')['accepted']>before and state('server')['serverShots']>shots,ownerAccepted=state('alpha')['accepted']-before,serverShots=state('server')['serverShots']-shots)
send(r,'alpha',op='screenshot',name='map-live')
# KillRace life latch, using explicitly logged server+owner position fixtures.
if expected=='Map_Depot55':
 send(r,'server',op='kill-fixture',target=cid);time.sleep(6)
 anchor=owner()['position'].copy();outside=dict(anchor);outside['x']+=18
 for role in ['server','alpha']:send(r,role,op='pose',target=cid,position=outside)
 time.sleep(1);p=owner()
 check('killrace-leave-lock',p['backpackEligibility']=='LockedByLeaving',reason=p['backpackEligibility'],position=p['position'])
 for role in ['server','alpha']:send(r,role,op='pose',target=cid,position=anchor)
 time.sleep(1);p=owner()
 check('killrace-return-stays-locked',p['backpackEligibility']=='LockedByLeaving',reason=p['backpackEligibility'],position=p['position'])
 send(r,'alpha',op='key',name='B');send(r,'alpha',op='screenshot',name='killrace-leave-denial')
 send(r,'server',op='kill-fixture',target=cid);time.sleep(6)
 check('killrace-respawn-restores',owner()['backpackEligibility']=='None',reason=owner()['backpackEligibility'])
 for weapon in ['pistol.day2','handgun.02','handgun.03','handgun.04']:
  for role in ['server','alpha','bravo']:send(r,role,op='equip',target=cid,weapon=weapon)
  for optic in ['attach.rifle.optic','attach.lpfp.optic.02']:
   for role in ['server','alpha','bravo']:send(r,role,op='attachment',target=cid,name=optic)
   send(r,'alpha',op='input',aim=True,duration=15,wait=1)
   send(r,'alpha',op='screenshot',name='ads-'+weapon+'-'+optic)
   send(r,'bravo',op='screenshot',name='tp-'+weapon+'-'+optic)
   check('ads-'+weapon+'-'+optic,owner()['weapon']==weapon and owner()['ads']>.95,ads=owner()['ads'])
  for role in ['server','alpha','bravo']:send(r,role,op='attachment',target=cid,name='')
send(r,'alpha',op='stop')
if not all(item['passed'] for item in results):sys.exit(2)
