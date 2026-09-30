import sys,time,json,shutil
from pathlib import Path
from realtest0930_commands import read,send
r=Path(sys.argv[1]);results=[]
def s(**kw):return send(r,'alpha',**kw)
def ui(name):return s(op='ui',name=name,wait=.8)
def keys(*names):
 for name in names:s(op='key',name=name)
def capture(name):return s(op='screenshot',name=name)
def check(name,ok,**kw):
 results.append(dict(case=name,passed=bool(ok),**kw));(r/'final-ui-results.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8');print(name,bool(ok),flush=True)
deadline=time.monotonic()+150
while time.monotonic()<deadline:
 if all((r/f'{role}.state.json').exists() for role in ['alpha','bravo','server']) and all(len(read(r/f'{role}.state.json')['players'])==2 for role in ['alpha','bravo','server']):break
 time.sleep(1)
else:raise RuntimeError('Final UI match timeout')
for role in ['alpha','bravo','server']:send(r,role,op='stop',fps=60)
keys('B');ui('BackpackTab2');p=next(p for p in read(r/'alpha.state.json')['players'] if p['owner']);check('final-mouse-switch',p['activeBackpack']==1)
for width in [1280,1920,2560]:
 s(op='resolution',slot=width,wait=.8);keys('B');capture('final-backpack-'+str(width));keys('Escape')
send(r,'server',op='end-match',wait=7);keys('Escape')
# Ending a match returns to the room; use its normal Leave action before
# validating lobby controls. Do not treat waiting-room UI as lobby UI.
s(op='ui-list');data=read(r/'alpha.ui.json')
leave=next((b for b in data['buttons'] if b['text']=='离开房间'),None)
if leave:ui(leave['name'])
deadline=time.monotonic()+20
while time.monotonic()<deadline:
 s(op='ui-list');data=read(r/'alpha.ui.json')
 if any(b['name']=='LobbyBackpackTab_3' for b in data['buttons']):break
 time.sleep(.5)
else:raise RuntimeError('Final UI did not return to lobby')
for width in [1280,1920,2560]:
 s(op='resolution',slot=width,wait=.8)
 ui('LobbyBackpackTab_3');capture('final-lobby-'+str(width))
 s(op='ui-list');data=read(r/'alpha.ui.json');shutil.copyfile(r/'alpha.ui.json',r/f'final-lobby-{width}.ui.json')
 check('lobby-tabs-'+str(width),all(any(b['text']=='背包 '+str(i) for b in data['buttons']) for i in [1,2,3]) and data['eventSystem'])
s(op='resolution',slot=1280,wait=.8);ui('Nav_仓库');ui('Category_Throwable');capture('final-warehouse')
s(op='ui-list');data=read(r/'alpha.ui.json');check('warehouse-three-slots',sum(b['name'].startswith('ThrowableSlot') for b in data['buttons'])==3)
ui('Nav_商城');ui('Category_Throwable');capture('final-shop')
s(op='ui-list');data=read(r/'alpha.ui.json');check('shop-five-models',sum(b['name'].startswith('WeaponCard_throwable.') for b in data['buttons'])==5)
if not all(x['passed'] for x in results):sys.exit(2)
