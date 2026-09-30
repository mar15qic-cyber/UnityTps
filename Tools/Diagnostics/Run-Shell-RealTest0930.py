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
for missing in range(1,7):
 case="reload-missing-"+str(missing);fixture(6-missing)
 send(run,"alpha",wait=.1,op="input",reload=True,duration=.2,caseId=case)
 samples=watch(case,1.8+missing*.7333333+.5)
 final={r:samples[-1][r] for r in ("alpha","server","bravo")}
 passed=all(p and p["ammo"]==6 and p["reserve"]==36-missing and p["reloadPhase"]=="None" for p in final.values())
 phases=sorted(set(s["server"]["reloadPhase"] for s in samples if s["server"]))
 report.append({"case":case,"passed":passed,"phases":phases,"final":{r:{k:p[k] for k in ("ammo","reserve","reloadPhase","runtimeState")} if p else None for r,p in final.items()}})
 print(json.dumps(report[-1]),flush=True)
 if not passed:break
(run/"shell-results.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
if not all(r["passed"] for r in report):sys.exit(1)
