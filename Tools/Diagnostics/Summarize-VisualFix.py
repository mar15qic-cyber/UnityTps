"""Index original screenshots and aggregate observational evidence (no image alterations)."""
import collections, html, json, sys
from pathlib import Path

root = Path(__file__).resolve().parents[2]
for name in sys.argv[1:]:
    run = root/'Logs/VisualFix0928'/name
    rows = [json.loads(line) for line in (run/'alpha.visual.jsonl').read_text().splitlines()]
    cases = collections.defaultdict(list)
    for row in rows: cases[row['caseId']].append(row)
    results = []
    for case, samples in cases.items():
        shots = [s for s in samples if s['traces'] > 0]
        held = [s for s in samples if s['phase'] in ['Equipped','Windup']]
        result = dict(case=case, samples=len(samples), activeTracerFrames=len(shots),
                      wrongLayerFrames=sum(s['wrongTracerLayers'] > 0 for s in shots),
                      maxPixelError=max((s['tracerPixelError'] for s in shots), default=None),
                      phases=dict(collections.Counter(s['phase'] for s in samples)),
                      heldVisibleFrames=sum(s['heldVisible'] for s in samples),
                      screenshots=[p.name for p in sorted(run.glob('alpha-'+case+'-*.png'))])
        results.append(result)
    (run/'visual-summary.json').write_text(json.dumps(results, indent=2))
    sections=[]
    for result in results:
        case=html.escape(result['case'])
        images=''.join('<figure><a href="'+html.escape(p)+'"><img loading="lazy" src="'+html.escape(p)+'"></a><figcaption>'+html.escape(p)+'</figcaption></figure>' for p in result['screenshots'])
        sections.append('<section><h2>'+case+'</h2><div>'+images+'</div></section>')
    (run/'screenshots.html').write_text('<!doctype html><meta charset="utf-8"><title>Original runtime evidence</title><style>body{background:#181b20;color:#eee;font:14px sans-serif;margin:24px}section>div{display:flex;flex-wrap:wrap}figure{margin:6px;width:480px}img{width:100%}figcaption{overflow-wrap:anywhere}</style><h1>'+html.escape(name)+'</h1><p>Original screenshots; click for full resolution. Metrics in visual-summary.json.</p>'+''.join(sections), encoding='utf-8')
    print(json.dumps(dict(run=name, cases=len(results), screenshots=sum(len(r['screenshots']) for r in results), activeFrames=sum(r['activeTracerFrames'] for r in results), wrongLayerFrames=sum(r['wrongLayerFrames'] for r in results), maxPixelError=max((r['maxPixelError'] for r in results if r['maxPixelError'] is not None), default=None))))
