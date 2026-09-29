from pathlib import Path
import json
import subprocess

root = Path('Logs/ThrowableReturn0929')
font = 'C\\:/Windows/Fonts/msyh.ttc'

def run(args):
    subprocess.run(['ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', *args], check=True)

for mode, title in [('auto', '投掷完成自动回枪'), ('manual', '手动从投掷物切回枪械')]:
    for speed, factor in [('normal', 1), ('slow', 2)]:
        inputs, filters = [], []
        for index, (weapon, variant) in enumerate([('rifle', 'before'), ('rifle', 'after'), ('pistol', 'before'), ('pistol', 'after')]):
            inputs += ['-framerate', '30', '-i', str(root / f'{weapon}-{mode}-{variant}' / 'frame-%03d.png')]
            label = ('步枪' if weapon == 'rifle' else '手枪') + (' · 修复前（旧逻辑复现）' if variant == 'before' else ' · 修复后')
            filters.append(f"[{index}:v]pad=640:396:0:36:color=0x141c26,drawtext=fontfile='{font}':text='{label}':fontcolor=white:fontsize=20:x=12:y=8[p{index}]")
        heading = title + ' · ' + ('正常速度' if factor == 1 else '半速') + ' · 编辑器动效预览'
        filters += ['[p0][p1][p2][p3]xstack=inputs=4:layout=0_0|640_0|0_396|640_396[grid]',
                    f"[grid]pad=1280:834:0:42:color=0x101821,drawtext=fontfile='{font}':text='{heading}':fontcolor=white:fontsize=24:x=14:y=9,setpts={factor}*PTS[out]"]
        run([*inputs, '-filter_complex', ';'.join(filters), '-map', '[out]', '-r', '30', '-c:v', 'libx264', '-crf', '18', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', str(root / f'{mode}-{speed}.mp4')])
    concat = root / f'{mode}-concat.txt'
    concat.write_text(f"file '{mode}-normal.mp4'\nfile '{mode}-slow.mp4'\n", encoding='ascii')
    run(['-f', 'concat', '-safe', '0', '-i', str(concat), '-c', 'copy', '-movflags', '+faststart', str(root / f'{mode}-comparison.mp4')])

summary = {}
for state_path in root.glob('*-*/states.json'):
    frames = json.loads(state_path.read_text(encoding='utf-8-sig'))
    transitions = []
    for frame in frames:
        if not transitions or frame['clip'] != transitions[-1]['clip']:
            transitions.append(frame)
    summary[state_path.parent.name] = transitions
(root / 'preview-state-transitions.json').write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding='utf-8')
print('Encoded auto/manual comparisons, normal + half speed; preview transition evidence saved.')
