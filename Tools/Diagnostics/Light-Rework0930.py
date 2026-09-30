import sys,time,json
from pathlib import Path
from realtest0930_commands import read,send
r=Path(sys.argv[1])
deadline=time.monotonic()+150
while time.monotonic()<deadline:
    try:
        a=read(r/'alpha.state.json');b=read(r/'bravo.state.json')
        if a['phase']=='InProgress' and len(a['players'])==2 and len(b['players'])==2:break
    except (FileNotFoundError,KeyError):pass
    time.sleep(.5)
else:raise RuntimeError('Light pair did not enter battle')
for role in ('alpha','bravo','server'):send(r,role,op='stop',fps=60)
pa=next(p for p in a['players'] if p['owner']);pb=next(p for p in b['players'] if p['owner'])
for role in ('server','alpha','bravo'):
    send(r,role,op='attachment',target=pa['connection'],name='')
for role in ('server','alpha','bravo'):
    send(r,role,op='equip',target=pb['connection'],weapon='rifle.02',slots=['rifle.02','pistol.day2'])
for role in ('server','alpha','bravo'):
    send(r,role,op='attachment',target=pb['connection'],name='attach.lpw.tactical.light')
origin=dict(pa['position']);origin['x']=7;origin['z']=-9;origin['y']=.05
for role,cid,pos in [('server',pa['connection'],origin),('alpha',pa['connection'],origin)]:
    send(r,role,op='pose',target=cid,position=pos,yaw=0)
remote=dict(origin);remote['x']+=.8;remote['z']+=.2
for role,cid,pos in [('server',pb['connection'],remote),('bravo',pb['connection'],remote)]:
    send(r,role,op='pose',target=cid,position=pos,yaw=0)
wall=dict(origin);wall['z']+=4;wall['y']+=1.5
for role in ('alpha','bravo','server'):
    send(r,role,op='geometry',name='remote-light-wall',position=wall,scale={'x':6,'y':3,'z':.2})
time.sleep(1)
send(r,'alpha',op='resolution',slot=1280)
send(r,'alpha',op='shadow-fixture',name='tp-legacy')
send(r,'alpha',op='screenshot',name='remote-light-hip')
send(r,'alpha',op='shadow-fixture',name='tp-filtered')
send(r,'alpha',op='screenshot',name='remote-light-hip-fixed')
send(r,'bravo',op='input',aim=True,duration=20)
time.sleep(1)
send(r,'alpha',op='shadow-fixture',name='tp-legacy')
send(r,'alpha',op='screenshot',name='remote-light-ads')
send(r,'alpha',op='shadow-fixture',name='tp-filtered')
send(r,'alpha',op='screenshot',name='remote-light-ads-fixed')
print('REMOTE_LIGHT_CAPTURED',flush=True)
