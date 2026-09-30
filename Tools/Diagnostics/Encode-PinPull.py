"""Encode synchronized, fixed-camera before/after renders; no frame synthesis."""
from pathlib import Path
import subprocess

ROOT = Path("Logs/PinPull0929")
FONT = "C\\:/Windows/Fonts/arial.ttf"


def run(args):
    subprocess.run(["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", *args], check=True)


for view in ("front",):
    for speed, rate, factor in (("normal", "1x", 1), ("slow", "0.5x", 2)):
        def label(side):
            return (f"pad=960:610:0:60:color=0x121923,"
                    f"drawtext=fontfile='{FONT}':text='{side} | {view.upper()} | {rate}':"
                    "fontcolor=white:fontsize=27:x=25:y=16")
        legend = "Pin separates at 0.12s | grenade releases at 0.35s | fixed camera"
        if view == "side":
            legend = "CYAN shoulder-elbow | ORANGE elbow-wrist | MAGENTA wrist | FORWARD ->"
        filters = (f"[0:v]{label('BEFORE')}[a];[1:v]{label('AFTER')}[b];"
                   f"[a][b]hstack=inputs=2,setpts={factor}*PTS,"
                   f"drawtext=fontfile='{FONT}':text='{legend}':fontcolor=white:fontsize=19:x=25:y=582[out]")
        run(["-framerate", "60", "-i", str(ROOT / "before" / view / "frame-%03d.png"),
             "-framerate", "60", "-i", str(ROOT / "after" / view / "frame-%03d.png"),
             "-filter_complex", filters, "-map", "[out]", "-r", "30", "-c:v", "libx264",
             "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
             str(ROOT / f"{view}-{speed}.mp4")])
    playlist = ROOT / f"{view}-concat.txt"
    playlist.write_text(f"file '{view}-normal.mp4'\nfile '{view}-normal.mp4'\nfile '{view}-slow.mp4'\n", encoding="ascii")
    run(["-f", "concat", "-safe", "0", "-i", str(playlist), "-c", "copy", "-movflags", "+faststart",
         str(ROOT / f"{view}-comparison.mp4")])

for phase, title in (("Frag", "FRAG"), ("after", "FLASH"), ("Smoke", "SMOKE"), ("quick", "QUICK CLICK - actual Animancer graph")):
    run(["-framerate", "60", "-i", str(ROOT / phase / "front" / "frame-%03d.png"),
         "-vf", f"drawtext=fontfile='{FONT}':text='{title}':fontcolor=white:fontsize=22:x=20:y=20",
         "-r", "30", "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
         str(ROOT / f"{phase}-preview.mp4")])
playlist = ROOT / "types-concat.txt"
playlist.write_text("file 'Frag-preview.mp4'\nfile 'after-preview.mp4'\nfile 'Smoke-preview.mp4'\n", encoding="ascii")
run(["-f", "concat", "-safe", "0", "-i", str(playlist), "-c", "copy", "-movflags", "+faststart", str(ROOT / "three-types-preview.mp4")])
print("Created synchronized before/after, three-type and quick-click previews.")
