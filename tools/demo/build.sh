#!/usr/bin/env bash
# build.sh <runDir> <out.mp4> [poster.jpg] : turns screencast frames into a constant-30fps H.264 mp4,
# and optionally writes a poster image (the title card, 1.5 s in).
set -euo pipefail
run=$1; out=$2; poster=${3:-}
python3 - "$run" <<'PY'
import json, sys, os
run = sys.argv[1]
d = json.load(open(os.path.join(run, 'frames.json')))
fr = d['frames']
lines = ["ffconcat version 1.0"]
for i, f in enumerate(fr):
    nxt = fr[i + 1]['t'] if i + 1 < len(fr) else max(d.get('end', 0), f['t'] + 0.5)
    lines.append(f"file 'frames/{f['file']}'")
    lines.append(f"duration {max(nxt - f['t'], 0.001):.4f}")
lines.append(f"file 'frames/{fr[-1]['file']}'")
open(os.path.join(run, 'list.ffconcat'), 'w').write("\n".join(lines) + "\n")
PY
ffmpeg -y -loglevel error -f concat -safe 0 -i "$run/list.ffconcat" \
  -vf "fps=30,scale=1920:1200:flags=lanczos,format=yuv420p" \
  -c:v libx264 -preset slow -crf 20 -movflags +faststart "$out"
ffprobe -v error -show_entries format=duration,size -of default=nw=1 "$out"
if [[ -n "$poster" ]]; then
  ffmpeg -y -loglevel error -ss 1.5 -i "$out" -frames:v 1 -vf scale=1280:-1 -q:v 3 "$poster"
fi
