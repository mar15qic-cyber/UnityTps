"""Summarize only runs matching the latest verified source identity."""
import csv
import json
from pathlib import Path

root=Path(__file__).resolve().parents[2]
log=root/'Logs/SystemFix0927'
expected=(log/'input-digest-before.txt').read_text().strip()
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def lines(p):
    if not p.exists(): return
    for line in p.read_text(encoding='utf-8-sig').splitlines():
        try: yield json.loads(line)
        except json.JSONDecodeError: pass

runs=[]; fps={}; all_pairs=[]
for directory in sorted(log.glob('fix*')):
    if not (directory/'analysis.json').exists() or not (directory/'server.state.json').exists():continue
    if read(directory/'server.state.json').get('inputDigest')!=expected:continue
    data=read(directory/'analysis.json')
    invalid={r['caseId'] for r in lines(directory/'cases.jsonl') if r.get('kind') in ('invalid-session','invalid-fixture')}
    completed={r['caseId'] for r in lines(directory/'cases.jsonl') if r.get('kind')=='case-end'}-invalid
    pairs=[r for r in data['pairedShots'] if r['caseId'] in completed]
    all_pairs+=pairs
    rows=[r for r in data['cases'] if r['caseId'] in completed]
    runs.append(dict(run=directory.name, completedCases=len(completed), invalidCases=sorted(invalid),
        predicted=sum(r['predicted'] for r in rows), accepted=sum(r['accepted'] for r in rows),
        rejected=sum(r['rejected'] for r in rows), paired=len(pairs), errors=data['errors']))
    for r in rows:
        if r['caseId'].startswith('fps-') and r['predicted']>=2:fps[r['caseId']]=dict(run=directory.name,**r)
summary=dict(inputDigest=expected,runs=runs,fpsCells=len(fps),
    pairedShots=len(all_pairs), seedMismatch=sum(not r['seedEqual'] for r in all_pairs),
    weaponMismatch=sum(not r['weaponEqual'] for r in all_pairs),
    spreadMismatch=sum(abs(r['spreadDifference'])>.01 for r in all_pairs),
    largestSpreadDifference=max((abs(r['spreadDifference']) for r in all_pairs),default=None),
    note='Only completed, non-invalid cases on the latest source identity. Preparation shots are included. Counts alone do not establish gameplay correctness.')
(log/'latest-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
(log/'fps-valid-matrix.json').write_text(json.dumps(list(fps.values()),indent=2),encoding='utf-8')
print(json.dumps(summary))
rates=list(csv.DictReader((root/'Assets/_Project/ScriptableObjects/Weapons/Tuning/MainlineWeaponTuning.csv').open(encoding='utf-8-sig')))
out=['# 修复后 FPS 数据（自动汇总）','','输入摘要：`'+expected+'`。仅汇总完整、未标记无效、同版本的用例；缺格记为未测。',
    '', '每格为实测 RPM / 发射期间 FPS。RPM 使用事件呈现时间，低帧补发保留同帧零间隔；短样本仍有帧量化误差。', '',
    '| 武器 | 配置 RPM | 15 | 30 | 60 | 120 | 144 | 240 |','|---|---:|---:|---:|---:|---:|---:|---:|']
for w in rates:
    cells=[]
    for n in (15,30,60,120,144,240):
        r=fps.get('fps-'+w['WeaponId']+'-'+str(n))
        cells.append(f"{r['rpm']:.1f} / {r['firingFps']:.1f}" if r and r['rpm'] and r['firingFps'] else '未测')
    out.append('| '+w['WeaponId']+' | '+w['Stat.Rpm']+' | '+' | '.join(cells)+' |')
(root/'Docs/审计/2026-09-27-修复后FPS数据.md').write_text('\n'.join(out)+'\n',encoding='utf-8')
