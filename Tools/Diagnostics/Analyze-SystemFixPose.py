"""Compare per-tick articulated snapshots without inferring success from missing data."""
import argparse
import json
import math
from pathlib import Path

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('directory', type=Path)
a = p.parse_args()
previous = {}
comparisons = 0
suspects = []
total = 0
with (a.directory / 'server.events.jsonl').open(encoding='utf-8-sig') as f:
    for line in f:
        try:
            r = json.loads(line)
        except json.JSONDecodeError:
            continue
        if r.get('kind') != 'pose-capture':
            continue
        total += 1
        key = (r['connection'], r['life'], r['caseId'])
        old = previous.get(key)
        previous[key] = r
        if old is None or old['frame'] != r['frame'] or old['serverTick'] == r['serverTick']:
            continue
        phase = abs((r['gaitPhase'] - old['gaitPhase'] + .5) % 1 - .5)
        if phase < .0001:
            continue
        volumes = {v['name']: v for v in old['capturedVolumes'] if v['enabled']}
        differences = [math.dist([v['center'][k] for k in 'xyz'],
                                [volumes[v['name']]['center'][k] for k in 'xyz'])
                       for v in r['capturedVolumes'] if v['enabled'] and v['name'] in volumes]
        if not differences:
            continue
        comparisons += 1
        if max(differences) < .00002:
            suspects.append(dict(caseId=r['caseId'], connection=r['connection'], frame=r['frame'],
                                 previousTick=old['serverTick'], tick=r['serverTick'],
                                 gaitAdvance=phase, maximumLocalBoneChange=max(differences)))
result = dict(records=total, sameFrameAdvancingGaitComparisons=comparisons,
              unchangedArticulatedPoseCount=len(suspects), examples=suspects[:30],
              interpretation='Unchanged bones while authoritative gait advances indicate render-phase reuse; zero comparisons is unverified.')
(a.directory / 'pose-analysis.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result))
