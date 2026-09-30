import json,time
from pathlib import Path

def read(path):
    end=time.monotonic()+4
    while True:
        try:return json.loads(Path(path).read_text(encoding="utf-8-sig"))
        except (PermissionError,FileNotFoundError,json.JSONDecodeError):
            if time.monotonic()>end:raise
            time.sleep(.02)

def send(run,role,wait=.45,**values):
    run=Path(run);s=read(run/f"{role}.state.json")
    old=read(run/f"{role}.command.json") if (run/f"{role}.command.json").exists() else {"seq":0}
    seq=max(s["seq"],old["seq"])+1
    p=run/f"{role}.command.tmp";p.write_text(json.dumps({"seq":seq,"caseId":"realtest0930",**values}),encoding="utf-8")
    p.replace(run/f"{role}.command.json")
    deadline=time.monotonic()+5
    while read(run/f"{role}.state.json")["seq"]<seq:
        if time.monotonic()>deadline:raise TimeoutError(role+" command not acknowledged")
        time.sleep(.05)
    time.sleep(wait)
    return read(run/f"{role}.state.json")
