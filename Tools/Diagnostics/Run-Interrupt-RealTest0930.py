import json,time,sys
from pathlib import Path
from realtest0930_commands import read,send
run=Path(sys.argv[1]);report=[]
def state(role):return read(run/(role+".state.json"))
def owner(role="alpha"):
 s=state(role);return next(p for p in s["players"] if p["owner"])
def fixture(ammo,reserve=36):
 target=owner()["connection"]
 for role in ("bravo","server","alpha"):
  send(run,role,wait=.05,op="equip",target=target,weapon="shotgun.01",slots=["shotgun.01","pistol.day2"])
 for role in ("server","alpha"):send(run,role,wait=.1,op="ammo",target=target,slot=ammo,health=reserve)
 return target
def watch(case,seconds):
 samples=[];end=time.monotonic()+seconds
 while time.monotonic()<end:
  rows={r:next((p for p in state(r)["players"] if p["connection"]==owner()["connection"]),None) for r in ("alpha","server","bravo")}
  samples.append({"utc":time.time(),**rows});time.sleep(.1)
 (run/(case+".timeline.json")).write_text(json.dumps(samples),encoding="utf-8")
 return samples

def verify(case,samples,ammo,reserve,shotDelta=None,before=0):
 final={r:samples[-1][r] for r in ("alpha","server","bravo")}
 passed=all(p and p["ammo"]==ammo and p["reserve"]==reserve and p["reloadPhase"]=="None" for p in final.values())
 if shotDelta is not None:passed=passed and state("server")["serverShots"]-before==shotDelta
 report.append({"case":case,"passed":passed,"final":{r:{k:p[k] for k in ("ammo","reserve","reloadPhase","runtimeState")} if p else None for r,p in final.items()},"serverShots":state("server")["serverShots"]-before})
 print(json.dumps(report[-1]),flush=True)
 (run/"interrupt-results.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
 if not passed:raise AssertionError(case)
case="reload-interrupt-loaded";fixture(2);before=state("server")["serverShots"]
send(run,"alpha",wait=.3,op="input",reload=True,duration=.1,caseId=case)
send(run,"alpha",wait=.1,op="input",fire=True,duration=.05,caseId=case)
verify(case,watch(case,2),1,36,1,before)
case="reload-interrupt-empty";fixture(0);before=state("server")["serverShots"]
send(run,"alpha",wait=.1,op="input",reload=True,duration=.1,caseId=case)
send(run,"alpha",wait=.1,op="input",fire=True,duration=.05,caseId=case)
verify(case,watch(case,3.5),0,35,1,before)
case="reload-limited-reserve";fixture(0,2)
send(run,"alpha",wait=.1,op="input",reload=True,duration=.1,caseId=case)
verify(case,watch(case,4.2),2,0)
case="reload-switch-cancel";fixture(0)
send(run,"alpha",wait=1.85,op="input",reload=True,duration=.1,caseId=case)
send(run,"alpha",wait=1,op="input",slot=1,duration=.1,caseId=case)
send(run,"alpha",wait=.8,op="input",slot=0,duration=.1,caseId=case)
verify(case,watch(case,1),1,35)
case="reload-death-cancel";target=fixture(0);oldlife=owner()["life"]
send(run,"alpha",wait=1.85,op="input",reload=True,duration=.1,caseId=case)
send(run,"server",wait=.2,op="kill-fixture",target=target,caseId=case)
samples=watch(case,4.5)
p=owner();passed=p["life"]>oldlife and not p["dead"] and p["reloadPhase"]=="None"
report.append({"case":case,"passed":passed,"oldLife":oldlife,"newLife":p["life"],"ammo":p["ammo"],"phase":p["reloadPhase"]});print(json.dumps(report[-1]),flush=True)
if not passed:raise AssertionError(case)
for seq,settings in ((1,{"delay_ms":120,"jitter_ms":80,"loss_percent":1}),(2,{"jitter_sequence_ms":[300,20,260,40],"loss_percent":0})):
 case="reload-network-"+str(seq)
 (run/"network.json").write_text(json.dumps({"seq":seq,"caseId":case,"seed":930,**settings}),encoding="utf-8")
 fixture(2);time.sleep(1)
 send(run,"alpha",wait=.1,op="input",reload=True,duration=.1,caseId=case)
 verify(case,watch(case,7),6,32)
(run/"network.json").write_text(json.dumps({"seq":3,"delay_ms":0,"caseId":"baseline-restored"}),encoding="utf-8")
(run/"interrupt-results.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
