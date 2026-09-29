from pathlib import Path
import subprocess

root=Path('Logs/ThrowableDraw0929')
font='C\\:/Windows/Fonts/msyh.ttc'
def run(args):
    subprocess.run(['ffmpeg','-y','-hide_banner','-loglevel','error',*args],check=True)
for mode in ('cycle','interrupt'):
    for speed,factor in (('normal',1),('slow',2)):
        title=('手雷 → 闪光弹 → 烟雾弹 → 手雷 → 投掷' if mode=='cycle' else '快速连切 → 拿出中直接投掷')
        title+=' · '+('正常速度' if factor==1 else '半速')
        label='三种投掷物共用同一拿出动画 · 不增加输入等待'
        filt=(f"pad=960:620:0:45:color=0x141c26,setpts={factor}*PTS,"
              f"drawtext=fontfile='{font}':text='{title}':fontcolor=white:fontsize=22:x=16:y=8,"
              f"drawtext=fontfile='{font}':text='{label}':fontcolor=white:fontsize=18:x=16:y=590")
        run(['-framerate','60','-i',str(root/mode/'frame-%03d.png'),'-vf',filt,'-r','30','-c:v','libx264','-crf','18','-pix_fmt','yuv420p','-movflags','+faststart',str(root/f'{mode}-{speed}.mp4')])
    playlist=root/f'{mode}-concat.txt'
    playlist.write_text(f"file '{mode}-normal.mp4'\nfile '{mode}-slow.mp4'\n",encoding='ascii')
    run(['-f','concat','-safe','0','-i',str(playlist),'-c','copy','-movflags','+faststart',str(root/f'{mode}-review.mp4')])
print('Shared draw previews encoded')
