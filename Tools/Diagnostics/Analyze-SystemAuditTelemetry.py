"""Join allowlisted tick evidence to the runtime probe, and summarize measured tick CPU time."""
import argparse
import bisect
import collections
import json
import math
import statistics
from pathlib import Path

def records(path):
    if not path.exists(): return
    with path.open(encoding='utf-8-sig') as f:
        for line in f:
            try: yield json.loads(line)
            except json.JSONDecodeError: pass

def percentile(values,p):
    return sorted(values)[max(0,math.ceil(len(values)*p)-1)] if values else None

def main():
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('directory',type=Path); args=p.parse_args(); d=args.directory
    cases={}; client_time={}; server_events=list(records(d/'server.events.jsonl'))
    for e in records(d/'alpha.events.jsonl'):
        if e['kind']=='predicted':
            key=(e['connection'],e['shotId']); cases[key]=e['caseId']; client_time[key]=e['utcMs']
    submits={}; results=[]; ticks=[]; reconciles=[]; server_step=[]; malformed=0
    for path in (d/'telemetry').glob('*.jsonl'):
        for e in records(path):
            if e['kind']=='shot-submit': submits[(e['connection'],e['shotId'])]=e
            elif e['kind'] in ('shot-result','shot-reject'): results.append(e)
            elif e['kind']=='server-tick-total': ticks.append(e['frameMs'])
            elif e['kind']=='server-tick': server_step.append(e['frameMs'])
            elif e['kind']=='reconcile': reconciles.append(e['error'])
    pairs=[]; grouped=collections.defaultdict(list)
    for e in results:
        key=(e['connection'],e['shotId']); s=submits.get(key)
        if not s: continue
        record=dict(caseId=cases.get(key),connection=key[0],shotId=key[1],kind=e['kind'],reason=e['reason'],
                    inputTick=s['inputTick'],displayTick=s['displayTick'],serverTick=e['serverTick'],
                    displayAgeMs=(e['serverTick']-s['displayTick'])*1000/30)
        pairs.append(record); grouped[record['caseId']].append(record)
    summary=[]
    for case,rows in grouped.items():
        ages=[r['displayAgeMs'] for r in rows]
        summary.append(dict(caseId=case,count=len(rows),accepted=sum(r['kind']=='shot-result' for r in rows),
                            reasons=dict(collections.Counter(r['reason'] for r in rows)),
                            displayAgeMin=min(ages),displayAgeMedian=statistics.median(ages),displayAgeMax=max(ages)))
    output=dict(run=d.name,shotTimes=pairs,shotTimeCases=summary,
                tickMeasuredWindow=dict(n=len(ticks),medianMs=percentile(ticks,.5),p95Ms=percentile(ticks,.95),p99Ms=percentile(ticks,.99),maxMs=max(ticks,default=None),
                    definition='ServerLagCompensation OnPreTick to capture and AfterCapture completion; excludes work outside this measured window'),
                playerSimulation=dict(n=len(server_step),p95Ms=percentile(server_step,.95),p99Ms=percentile(server_step,.99)),
                reconcileError=dict(n=len(reconciles),p95Meters=percentile(reconciles,.95),p99Meters=percentile(reconciles,.99),maxMeters=max(reconciles,default=None),
                    limitation='Includes explicitly logged pose fixtures; these maxima are not natural-movement defects'))
    (d/'telemetry-analysis.json').write_text(json.dumps(output,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(run=d.name,tick=output['tickMeasuredWindow'],shots=len(pairs),cases=summary),ensure_ascii=False))

if __name__=='__main__': main()
