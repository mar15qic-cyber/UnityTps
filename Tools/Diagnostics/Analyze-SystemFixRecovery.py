"""Measure movement recovery and action epochs without conflating fixture teleports."""
import json, math, sys
from collections import defaultdict
from pathlib import Path

d=Path(sys.argv[1]); events=defaultdict(list)
for line in (d/'cases.jsonl').read_text(encoding='utf-8-sig').splitlines():
    try: e=json.loads(line)
    except json.JSONDecodeError: continue
    if 'states' in e: events[e['caseId']].append(e)
rows=[]
for case, items in events.items():
    last=next((e for e in reversed(items) if e['kind']=='case-end'),None)
    if last is None: continue
    states=last['states']; owners=[p for p in states['alpha']['players'] if p['owner']]
    if not owners: continue
    owner=owners[0]
    authority=next((p for p in states['server']['players'] if p['connection']==owner['connection']),None)
    if authority is None: continue
    row=dict(caseId=case, ownerWeapon=owner['weapon'],serverWeapon=authority['weapon'],
        ownerAmmo=owner['ammo'], serverAmmo=authority['ammo'],ownerAction=owner['action'],serverAction=authority['action'],
        ownerGrounded=owner['grounded'],serverGrounded=authority['grounded'],
        finalPositionDifference=math.dist([owner['position'][k] for k in 'xyz'],[authority['position'][k] for k in 'xyz']),
        ownerInputTick=owner['inputTick'], serverInputTick=authority['inputTick'],
        ownerEquipment=owner.get('submittedEquipment'),serverEquipment=authority.get('executedEquipment'))
    if case.startswith(('movement-','burst-jump','action-','duplicate-','rejected-','corpse-','death-','cover-','stairs-')):
        rows.append(row)
output=dict(run=d.name,cases=rows,note='End snapshots are sampled asynchronously. Position/input differences include network and render delay; compare settled state and raw timeline before diagnosing.')
(d/'recovery-analysis.json').write_text(json.dumps(output,indent=2),encoding='utf-8')
print(json.dumps(output))
