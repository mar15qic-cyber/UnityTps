"""Encode actual Animancer renders at normal/half speed; no generated frames."""
from pathlib import Path
import subprocess

root=Path('Logs/PinMotion0929')
font='C\\:/Windows/Fonts/msyh.ttc'
def run(args):
    subprocess.run(['ffmpeg','-y','-hide_banner','-loglevel','error',*args],check=True)
def encode(kind,mode,view,speed):
    name=f'{kind}-{mode}-{view}-{speed}'
    label=('长按准备后松手' if mode=='hold' else '直接短按')+(' · 第一人称' if view=='front' else ' · 斜侧调试')+(' · 正常速度' if speed=='normal' else ' · 半速')
    footer='待评审动作稿 · 固定镜头 · 原有出手时序'
    if view=='side': footer='青：左上臂  橙：左前臂  紫：左腕 · 保留右手投掷主体'
    filt=(f"pad=960:620:0:45:color=0x141c26,setpts={1 if speed=='normal' else 2}*PTS,"
          f"drawtext=fontfile='{font}':text='{label}':fontcolor=white:fontsize=24:x=18:y=8,"
          f"drawtext=fontfile='{font}':text='{footer}':fontcolor=white:fontsize=18:x=18:y=590")
    run(['-framerate','60','-i',str(root/kind/mode/view/'frame-%03d.png'),'-vf',filt,'-r','30',
         '-c:v','libx264','-crf','18','-pix_fmt','yuv420p','-movflags','+faststart',str(root/(name+'.mp4'))])
    return name
def concat(name,clips):
    playlist=root/(name+'.txt')
    playlist.write_text(''.join(f"file '{clip}.mp4'\n" for clip in clips),encoding='ascii')
    run(['-f','concat','-safe','0','-i',str(playlist),'-c','copy','-movflags','+faststart',str(root/(name+'.mp4'))])

hold=encode('Frag','hold','front','normal')
holdslow=encode('Frag','hold','front','slow')
quick=encode('Frag','quick','front','normal')
quickslow=encode('Frag','quick','front','slow')
concat('motion-review',[hold,hold,holdslow,quick,quickslow])
side=encode('Frag','hold','side','normal')
sideslow=encode('Frag','hold','side','slow')
concat('side-review',[side,side,sideslow])
flash=encode('Flash','hold','front','normal')
smoke=encode('Smoke','hold','front','normal')
concat('three-types-review',[hold,flash,smoke])
print('Motion review videos ready')
