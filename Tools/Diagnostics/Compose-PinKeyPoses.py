from pathlib import Path
import subprocess

root = Path('Logs/PinKeyPoses0929')
font = 'C\\:/Windows/Fonts/msyh.ttc'
titles = ['01  即将接触', '02  即将拔开', '03  刚刚分离']
notes = ['右手已开始转雷，左手背朝上接近', '拇指、食指接环，双手建立支撑', '沿销轴方向拉开，接向左下退场']
for view in ('front', 'side'):
    height = 260 if view == 'front' else 405
    inputs, filters = [], []
    for i in range(3):
        inputs += ['-i', str(root / 'draft' / view / f'frame-{i:03}.png')]
        if view == 'front':
            # Same crop for every frame; no per-pose recentering or camera change.
            resize = 'crop=720:260:200:280'
        else:
            resize = 'scale=720:405'
        note = notes[i] if view == 'front' else '青：左上臂  橙：左前臂  紫：左腕'
        filters.append(f"[{i}:v]{resize},pad=720:{height+110}:0:56:color=0x141c26,"
            f"drawtext=fontfile='{font}':text='{titles[i]}':fontsize=27:fontcolor=white:x=20:y=10,"
            f"drawtext=fontfile='{font}':text='{note}':fontsize=22:fontcolor=white:x=20:y={height+69}[v{i}]")
    filters.append('[v0][v1][v2]hstack=inputs=3[out]')
    subprocess.run(['ffmpeg','-y','-hide_banner','-loglevel','error',*inputs,
        '-filter_complex',';'.join(filters),'-map','[out]','-frames:v','1',str(root/f'keyposes-{view}.png')],check=True)
print('Saved three-key-pose review sheets')
