"""Compact exact-case evidence without dumping player volumes or credentials."""
import argparse
import collections
import json
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);p.add_argument('case');a=p.parse_args()
for role in ('server','alpha','bravo','charlie'):
    f=a.directory/(role+'.events.jsonl')
    if not f.exists():continue
    counts=collections.Counter();shots=[];states=[];messages=[]
    for l in f.open(encoding='utf-8-sig'):
        try:r=json.loads(l)
        except ValueError:continue
        if r.get('caseId')!=a.case:continue
        counts[r['kind']]+=1
        if r['kind'] in ('predicted','server-shot','confirm'):
            shots.append({k:r.get(k) for k in ('kind','shotId','weapon','accepted','damage','ads','spread','time')})
        if r['kind']=='state':
            s=r['snapshot'];states.append(dict(time=s['time'],phase=s['phase'],players=[{k:v.get(k) for k in
                ('connection','owner','weapon','ammo','action','dead','health','enabledDamageVolumes','life','position')} for v in s['players']]))
        if r['kind']=='game-log' and ('reject id=' in r.get('message','') or 'SwitchTrace' in r.get('message','')):
            messages.append(r['message'])
    print(json.dumps(dict(role=role,counts=dict(counts),shots=shots[:8],messages=messages[:8],
        first=states[:1],last=states[-1:]),ensure_ascii=False))
