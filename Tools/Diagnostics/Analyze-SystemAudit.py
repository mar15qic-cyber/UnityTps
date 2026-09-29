"""Produce bounded summaries from raw runtime evidence; never infer pass from an empty case."""
import argparse
import collections
import json
import math
import re
import statistics
from pathlib import Path


def lines(path):
    if not path.exists(): return
    with path.open(encoding='utf-8-sig') as f:
        for line in f:
            try: yield json.loads(line)
            except json.JSONDecodeError: pass


def angle(a,b):
    av=[a.get(k,0) for k in 'xyz']; bv=[b.get(k,0) for k in 'xyz']
    den=math.sqrt(sum(x*x for x in av)*sum(x*x for x in bv))
    return math.degrees(math.acos(max(-1,min(1,sum(x*y for x,y in zip(av,bv))/den)))) if den else None


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory',type=Path)
    args=parser.parse_args(); d=args.directory
    cases=collections.defaultdict(lambda:dict(predicted=[],predFrames=[],samples=[],accepted=0,rejected=0,server=[],fps=[],rtt=[],reasons=collections.Counter(),states={}))
    actual={}; predicted={}; errors=collections.Counter(); totals=collections.Counter(); phases=collections.Counter()
    for role in ('server','alpha','bravo','charlie'):
        for r in lines(d/(role+'.events.jsonl')):
            kind=r.get('kind'); c=cases[r.get('caseId','')]; totals[role+':'+str(kind)]+=1
            if kind=='predicted':
                predicted[(r['connection'],r['shotId'])]=r
                if role=='alpha': c['predicted'].append(r['time']); c['predFrames'].append(r['frame'])
            if kind=='server-shot':
                actual[(r['connection'],r['shotId'])]=r; c['server'].append(r['time'])
            if kind=='confirm' and role=='alpha': c['accepted' if r['accepted'] else 'rejected']+=1
            if kind=='command-error': errors[r.get('message','')[:300]]+=1
            if kind=='game-log':
                m=re.search(r'reject id=\d+.*reason=(\w+)',r.get('message',''))
                if m and role=='server': c['reasons'][m[1]]+=1
                if 'Exception' in r.get('message',''): errors[r['message'][:300]]+=1
            if kind=='state':
                s=r['snapshot']; c['states'][role]=s; phases[role+':'+s['phase']]+=1
                if role=='alpha':
                    c['fps'].append(s['measuredFps']); c['rtt'].append(s['rtt'])
                    c['samples'].append((s['time'],s['measuredFps'],s['rtt']))
    result=[]
    for name,c in cases.items():
        # Low-FPS catch-up may produce two valid shots in one render frame. Keep zero
        # frame-time intervals; discarding them silently undercounts the corrected RPM.
        times=c['predicted']; dt=[b-a for a,b in zip(times,times[1:]) if b>=a]
        fps=c['fps']; rtt=c['rtt']
        firing_rtt=[min(c['samples'],key=lambda s:abs(s[0]-t))[2] for t in times] if c['samples'] else []
        summary=dict(caseId=name,predicted=len(times),accepted=c['accepted'],rejected=c['rejected'],serverShots=len(c['server']),
                     rpm=60/statistics.mean(dt) if dt and sum(dt)>0 else None,
                     firingFps=(c['predFrames'][-1]-c['predFrames'][0])/(times[-1]-times[0]) if len(times)>1 and times[-1]>times[0] else None,
                     firingRtt=statistics.median(firing_rtt) if firing_rtt else None,
                     medianFps=statistics.median(fps) if fps else None,minFps=min(fps) if fps else None,
                     medianRtt=statistics.median(rtt) if rtt else None,maxRtt=max(rtt) if rtt else None,
                     rejectReasons=dict(c['reasons']), final={})
        for role,s in c['states'].items():
            summary['final'][role]=dict(phase=s['phase'],players=[{k:p.get(k) for k in ('connection','owner','health','dead','life','protectedNow','weapon','ammo','reserve','action','position','enabledDamageVolumes','damageVolumes','kills','deaths','ads','bloom','pitch','recoil','debt')} for p in s['players']])
        result.append(summary)
    paired=[]
    for key,p in predicted.items():
        s=actual.get(key)
        if s:
            paired.append(dict(caseId=p['caseId'],connection=key[0],shotId=key[1],seedEqual=p['seed']==s['seed'],
                               weaponEqual=p['weapon']==s['weapon'],clientWeapon=p['weapon'],serverWeapon=s['weapon'],
                               spreadDifference=s['spread']-p['spread'],adsDifference=s['ads']-p['ads'],
                               directionAngle=angle(p['fired'],s['fired']),clientHit=p['hit'],serverHit=s['hit'],serverDamage=s['damage']))
    output=dict(run=d.name,totals=dict(totals),phases=dict(phases),errors=dict(errors),cases=result,pairedShots=paired)
    (d/'analysis.json').write_text(json.dumps(output,ensure_ascii=False,indent=2),encoding='utf-8')
    brief=[{k:c[k] for k in ('caseId','predicted','accepted','rejected','serverShots','rpm','firingFps','firingRtt','medianFps','medianRtt','rejectReasons')} for c in result]
    print(json.dumps(dict(run=d.name,errors=dict(errors),cases=brief,paired=len(paired),
                         seedMismatch=sum(not x['seedEqual'] for x in paired),weaponMismatch=sum(not x['weaponEqual'] for x in paired),
                         spreadMismatch=sum(abs(x['spreadDifference'])>.01 for x in paired)),ensure_ascii=False))


if __name__=='__main__': main()
