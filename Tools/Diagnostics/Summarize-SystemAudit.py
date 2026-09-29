"""Generate review tables from preserved audit evidence; never launches a game."""
import collections
import csv
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LOG = ROOT / 'Logs/SystemAudit0927'
DOC = ROOT / 'Docs/审计/2026-09-27-实机测试-数据附录.md'

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def link(path, label=None):
    return '[' + (label or path.name) + '](' + path.as_posix() + ')'

def records(path):
    for line in path.open(encoding='utf-8-sig'):
        yield json.loads(line)

def main():
    analysis = {n: read(LOG/n/'analysis.json') for n in ('run05','run06','run07','run08')}
    rates = list(csv.DictReader((ROOT/'Assets/_Project/ScriptableObjects/Weapons/Tuning/MainlineWeaponTuning.csv').open(encoding='utf-8-sig')))
    selected = {}
    for run in ('run05', 'run07'):
        for row in analysis[run]['cases']:
            if row['caseId'].startswith('fps-') and row['predicted'] >= 2:
                selected[row['caseId']] = dict(run=run, **row)
    assert len(selected) == 96
    assert sum(c['predicted'] for c in selected.values()) == 1204
    assert all(c['predicted'] == c['accepted'] and c['rejected'] == 0 for c in selected.values())
    (LOG/'fps-valid-matrix.json').write_text(json.dumps(list(selected.values()), ensure_ascii=False, indent=2), encoding='utf-8')
    out = ['# 实机测试数据附录（2026-09-27）', '', '由 Summarize-SystemAudit.py 从留存日志生成。以下是测量结果，不等于所有用例通过。', '',
           '## 16 枪 × 6 档 FPS', '', '每格为 **RPM / 开火期间实际 FPS**。开火 FPS = 首末发 frame 差 / time 差；RPM = 60 × 发射间隔数 / 首末发时间差。目标档位不等于稳定实测帧率。', '',
           '96 格有效样本，共 1204 发预测和 1204 发权威接受。run05 首次 6 格空弹匣夹具无效，已以 run07 同名补测替换；不把夹具错误计为产品 Bug。狙击采样较短、发数少，只可作本轮观察。', '',
           '| WeaponId | 配置 RPM | 目标15 | 目标30 | 目标60 | 目标120 | 目标144 | 目标240 |', '|---|---:|---:|---:|---:|---:|---:|---:|']
    for w in rates:
        cells=[]
        for fps in (15,30,60,120,144,240):
            c=selected['fps-'+w['WeaponId']+'-'+str(fps)]
            cells.append(f"{c['rpm']:.1f} / {c['firingFps']:.1f}" + (' *' if c['run']=='run07' else ''))
        out.append('| '+w['WeaponId']+' | '+w['Stat.Rpm']+' | '+' | '.join(cells)+' |')
    out += ['', '* 表示 run07 补测。逐格发数、来源、RTT 和最终状态见 '+link(LOG/'fps-valid-matrix.json')+'。', '', '## 网络矩阵', '',
            'run06 v2；delay 为额外单程延迟。人物两发使用 Glock，world-auto 使用 UZI，因此本表不能单独隔离“瞄人”这一变量；同枪隔离证据见后表。loss 组均叠加单程 25 ms，并且在 delay100 之后连续执行，不能当成互相独立的全新连接实验。', '',
            '| 用例 | 预测 | 接受 | 拒绝 | 开火 RTT 中位数 ms | 拒绝原因 |', '|---|---:|---:|---:|---:|---|']
    for c in analysis['run06']['cases']:
        if c['caseId'].startswith('v2-network-'):
            out.append(f"| {c['caseId']} | {c['predicted']} | {c['accepted']} | {c['rejected']} | {c['firingRtt']} | {json.dumps(c['rejectReasons'])} |")
    out += ['', '## 同枪瞄人/对空隔离', '', 'run08；保留同一把 Glock，发射之间只改变虚拟鼠标瞄准方向，不换枪或重置装备。年龄按 30 Hz 网络 tick 换算。', '',
            '| 用例 | ShotId | 回溯年龄 ms | 服务器结果 |', '|---|---:|---:|---|']
    for r in read(LOG/'run08/telemetry-analysis.json')['shotTimes']:
        if (r['caseId'] or '').startswith('live-aim-'):
            out.append(f"| {r['caseId']} | {r['shotId']} | {r['displayAgeMs']:.3f} | {r['reason']} |")
    out += ['', '## Tick 测量窗口', '', '不是整帧 CPU 时间：只统计 ServerLagCompensation.OnPreTick 到 capture/AfterCapture 完成的窗口。含夹具与诊断开销，不是满服压测或正式包性能验收。', '',
            '| Run | 样本 | P50 ms | P95 ms | P99 ms | 最大 ms |', '|---|---:|---:|---:|---:|---:|']
    for n in analysis:
        t=read(LOG/n/'telemetry-analysis.json')['tickMeasuredWindow']
        out.append(f"| {n} | {t['n']} | {t['medianMs']:.3f} | {t['p95Ms']:.3f} | {t['p99Ms']:.3f} | {t['maxMs']:.3f} |")
    out += ['', '## 回归测试完整失败清单', '', '全量失败和后续复跑分开保留，复跑通过不抹去全量失败。断言失败本身不自动等于已定位的 Gameplay Bug。', '']
    for name in ('editmode-full-result.json','playmode-full-result.json','editmode-failed-recheck.json'):
        path=LOG/name
        if not path.exists():continue
        r=read(path)
        out += ['### '+name, '', '`'+json.dumps(r['summary'],ensure_ascii=False)+'`', '', link(path,'完整原始结果（含通过项及堆栈）'), '', '| 用例 | 失败信息 |', '|---|---|']
        for t in r['results']:
            if t['state'].startswith('Failed'):
                message=t.get('message','').strip().replace('\r','').replace('\n',' / ').replace('|','\\|')
                out.append('| '+t['fullName']+' | '+message+' |')
        out.append('')
    DOC.write_text('\n'.join(out)+'\n',encoding='utf-8')
    print(json.dumps(dict(validFpsCells=len(selected),shots=1204,appendix=str(DOC)),ensure_ascii=False))

if __name__=='__main__': main()
