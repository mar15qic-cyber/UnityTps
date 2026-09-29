import collections, json
from pathlib import Path

root=Path(__file__).resolve().parents[2]
run=root/'Logs/PreviewV1-r2/runtime-round1'
def records(path):
    if not path.exists(): return []
    result=[]
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        try: result.append(json.loads(line))
        except ValueError: pass
    return result
rows=records(run/'alpha.visual.jsonl')
groups=collections.defaultdict(list)
for row in rows: groups[row['caseId']].append(row)
summary=[]
for name, values in sorted(groups.items()):
    trace=[v for v in values if v['traces']>0]
    bore=[v['boreError'] for v in values if v['boreError']>=0]
    throw='-throw' in name
    phases=sorted(set(v['phase'] for v in values))
    ready=[v for v in values if v['phase']=='Selected' and not v['throwing']]
    result=dict(case=name,samples=len(values),tracerSamples=len(trace),
        maxBoreErrorMm=max(bore,default=-1)*1000,
        maxTracerPixelError=max((v['tracerPixelError'] for v in trace),default=-1),
        exits=sorted(set(v['exit'] for v in values)))
    result['actualWeapons']=sorted(set(v['weapon'] for v in values))
    result['bareFixtureHasAttachment']=('-bare-' in name and any('Att_' in e for e in result['exits']))
    if throw:
        result.update(phases=phases,readySamples=len(ready),visibleReadySamples=sum(v['heldVisible'] for v in ready),
            throwSamples=sum(v['throwing'] for v in values))
        result['passed']=bool(ready) and any(v['heldVisible'] for v in ready) and any(v['throwing'] for v in values) and 'Released' in phases
    else:
        result['passed']=bool(trace) and bool(bore) and max(bore)<.0005 and result['maxTracerPixelError']<.25
        if '-suppressor-' in name: result['passed'] &= all('Att_' in e for e in result['exits'])
    result['numericCheckPassed']=result.pop('passed')
    summary.append(result)
events={role:records(run/(role+'.events.jsonl')) for role in ['alpha','bravo','server']}
eventCounts={role:dict(collections.Counter(r['kind'] for r in values)) for role,values in events.items()}
telemetry={}
for role in ['alpha','bravo','server']:
    files=list((run/(role+'-telemetry')).rglob('*.jsonl'))
    values=[v for p in files for v in records(p)]
    telemetry[role]=dict(files=len(files),events=len(values),kinds=dict(collections.Counter(v.get('kind') for v in values)))
maps={role:json.loads((run/(role+'.map-visual.json')).read_text()) for role in ['alpha','bravo'] if (run/(role+'.map-visual.json')).exists()}
result=dict(campaign=json.loads((run/'campaign-result.json').read_text()),
    cases=summary,numericChecksPassed=sum(r['numericCheckPassed'] for r in summary),numericChecksFailed=[r['case'] for r in summary if not r['numericCheckPassed']],
    visualAcceptancePassed=False,
    visualFindings=['Yellow shot effect remains below visible muzzle in captured frames, including suppressor cases.',
                    'Released throwable appears very large in front of camera in captured frames; throw visuals not accepted.'],
    maps=maps,eventCounts=eventCounts,telemetry=telemetry,
    commandErrors={role:[r.get('message') for r in values if r['kind']=='command-error'] for role,values in events.items()},
    limitations=['Local machine and overlay interface, not a geographically remote network test.',
                 'Weapon/attachment matrix uses explicitly logged equipment fixtures.',
                 'Some bare fixtures inherited visual attachments; do not interpret numeric checks as full bare/suppressed acceptance.',
                 'Test copies use exact release gameplay DLLs/assets plus a local-only observation assembly.'])
(run/'summary.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k not in ['cases','campaign']},indent=2))
