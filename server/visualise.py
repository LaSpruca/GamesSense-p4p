"""
Turn saved haptic clips into a visual report you can open in a browser.

Every clip is drawn as the controller amplitude envelope the Quest would actually play -- the
envelope maths comes from e2e_test.to_amplitudes, which is a port of WaveformToAmplitudes.cs, so
the picture is what the hardware does, not an approximation of the audio.

    python visualise.py --glob "%TEMP%/scene-*.wav" --out haptic-report.html

Stdlib only, self-contained HTML output (no CDN, no build step).
"""

from __future__ import annotations

import argparse
import glob
import html
import os

import e2e_test as client          # decode_wav + to_amplitudes: same maths as the headset
import haptic_quality             # same accept/reject thresholds as the server

try:
    import scenes as scene_lib
    SCENE_NOTES = {s["name"]: s.get("note", "") for s in scene_lib.SCENES}
except Exception:
    SCENE_NOTES = {}

# --- palette (dataviz reference instance; validated with scripts/validate_palette.js) ----------
LIGHT = {
    "surface": "#fcfcfb", "plane": "#f9f9f7", "primary": "#0b0b0b", "secondary": "#52514e",
    "muted": "#898781", "grid": "#e1e0d9", "axis": "#c3c2b7", "series": "#2a78d6",
    "good": "#0ca30c", "critical": "#d03b3b", "warning": "#fab219",
    "border": "rgba(11,11,11,0.10)",
}
DARK = {
    "surface": "#1a1a19", "plane": "#0d0d0d", "primary": "#ffffff", "secondary": "#c3c2b7",
    "muted": "#898781", "grid": "#2c2c2a", "axis": "#383835", "series": "#3987e5",
    "good": "#0ca30c", "critical": "#d03b3b", "warning": "#fab219",
    "border": "rgba(255,255,255,0.10)",
}

PLOT_W, PLOT_H = 300, 96
PAD_L, PAD_R, PAD_T, PAD_B = 30, 8, 8, 20
SVG_W = PLOT_W + PAD_L + PAD_R
SVG_H = PLOT_H + PAD_T + PAD_B

# Status is carried by icon + label, never hue alone: the reference palette's `critical` and
# `good` sit at CVD deltaE 4.1 (deuteranopia), so red/green is not a distinguishing channel.
STATUS = {
    "shaped": ("check", "shaped", "good"),
    "drone":  ("alert", "drone",  "critical"),
    "silent": ("alert", "silent", "warning"),
}


def analyse(path):
    samples, rate = client.decode_wav(open(path, "rb").read())
    amps = client.to_amplitudes(samples, rate)
    peak = max((abs(s) for s in samples), default=0.0)
    rms = (sum(s * s for s in samples) / len(samples)) ** 0.5 if samples else 0.0
    name = os.path.basename(path).rsplit(".", 1)[0]
    for prefix in ("scene-", "haptic-"):
        if name.startswith(prefix):
            name = name[len(prefix):]
    return {
        "name": name,
        "note": SCENE_NOTES.get(name, ""),
        "amps": amps,
        "duration": len(samples) / rate if rate else 0.0,
        "peak": peak,
        "rms": rms,
        "crest": haptic_quality.crest_factor(peak, rms),
        "status": haptic_quality.classify(peak, rms),
        "mean_env": sum(amps) / len(amps) if amps else 0.0,
        "perceptible": 100 * sum(1 for a in amps if a >= 13) / len(amps) if amps else 0.0,
    }


def sparkpath(amps):
    """Envelope as an SVG polyline + a closed area path, in plot coordinates."""
    if not amps:
        return "", ""
    n = len(amps)
    pts = []
    for i, a in enumerate(amps):
        x = PAD_L + (i / max(n - 1, 1)) * PLOT_W
        y = PAD_T + PLOT_H - (a / 255) * PLOT_H
        pts.append(f"{x:.2f},{y:.2f}")
    line = " ".join(pts)
    base = PAD_T + PLOT_H
    area = f"M{PAD_L:.2f},{base:.2f} L" + " L".join(pts) + f" L{PAD_L + PLOT_W:.2f},{base:.2f} Z"
    return line, area


def facet(clip, idx):
    line, area = sparkpath(clip["amps"])
    icon, label, tone = STATUS[clip["status"]]
    peak_i = max(range(len(clip["amps"])), key=lambda i: clip["amps"][i]) if clip["amps"] else 0
    peak_v = clip["amps"][peak_i] if clip["amps"] else 0
    peak_x = PAD_L + (peak_i / max(len(clip["amps"]) - 1, 1)) * PLOT_W
    peak_y = PAD_T + PLOT_H - (peak_v / 255) * PLOT_H

    gridlines = "".join(
        f'<line class="grid" x1="{PAD_L}" y1="{PAD_T + PLOT_H - f * PLOT_H:.1f}" '
        f'x2="{PAD_L + PLOT_W}" y2="{PAD_T + PLOT_H - f * PLOT_H:.1f}"/>'
        for f in (0.5, 1.0)
    )
    ticks = "".join(
        f'<text class="tick" x="{PAD_L - 6}" y="{PAD_T + PLOT_H - f * PLOT_H + 3.5:.1f}" '
        f'text-anchor="end">{v}</text>'
        for f, v in ((0.0, "0"), (0.5, "128"), (1.0, "255"))
    )
    icon_svg = ('<path d="M3 8.5l3.2 3.2L13 4.5" fill="none" stroke="currentColor" '
                'stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/>'
                if icon == "check" else
                '<path d="M8 2.8l6 10.4H2z" fill="none" stroke="currentColor" stroke-width="2" '
                'stroke-linejoin="round"/><path d="M8 6.6v3.1" stroke="currentColor" '
                'stroke-width="2" stroke-linecap="round"/><circle cx="8" cy="11.4" r="1.05" '
                'fill="currentColor"/>')

    return f"""
    <figure class="facet" data-idx="{idx}">
      <figcaption>
        <div class="facet-head">
          <span class="facet-name">{html.escape(clip['name'])}</span>
          <span class="badge badge-{tone}">
            <svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true">{icon_svg}</svg>
            {label}
          </span>
        </div>
        <p class="facet-note">{html.escape(clip['note'] or 'controller amplitude over time')}</p>
      </figcaption>
      <svg viewBox="0 0 {SVG_W} {SVG_H}" class="plot" role="img"
           aria-label="{html.escape(clip['name'])}: peak {peak_v} of 255, {clip['status']}">
        {gridlines}
        <line class="axis" x1="{PAD_L}" y1="{PAD_T + PLOT_H}" x2="{PAD_L + PLOT_W}" y2="{PAD_T + PLOT_H}"/>
        <path class="area" d="{area}"/>
        <polyline class="line" points="{line}"/>
        <circle class="peak-dot" cx="{peak_x:.1f}" cy="{peak_y:.1f}" r="4"/>
        <text class="peak-label" x="{min(peak_x + 8, PAD_L + PLOT_W - 20):.1f}"
              y="{max(peak_y - 6, PAD_T + 9):.1f}">{peak_v}</text>
        {ticks}
        <text class="tick" x="{PAD_L}" y="{SVG_H - 6}">0s</text>
        <text class="tick" x="{PAD_L + PLOT_W}" y="{SVG_H - 6}" text-anchor="end">{clip['duration']:.2f}s</text>
        <line class="crosshair" x1="0" y1="{PAD_T}" x2="0" y2="{PAD_T + PLOT_H}" style="display:none"/>
        <rect class="hit" x="{PAD_L}" y="{PAD_T}" width="{PLOT_W}" height="{PLOT_H}" fill="transparent"/>
      </svg>
      <div class="tip" hidden></div>
    </figure>"""


def table_rows(clips):
    out = []
    for c in clips:
        _, label, tone = STATUS[c["status"]]
        out.append(
            f"<tr><td>{html.escape(c['name'])}</td>"
            f"<td class='num'>{c['duration']:.2f}</td>"
            f"<td class='num'>{c['peak']:.3f}</td>"
            f"<td class='num'>{c['rms']:.3f}</td>"
            f"<td class='num'>{c['crest']:.1f}</td>"
            f"<td class='num'>{c['mean_env']:.0f}</td>"
            f"<td class='num'>{c['perceptible']:.0f}%</td>"
            f"<td><span class='badge badge-{tone}'>{label}</span></td></tr>"
        )
    return "\n".join(out)


def css_vars(pal):
    return "\n".join(f"    --{k}: {v};" for k, v in pal.items())


def build(clips, title):
    drones = sum(1 for c in clips if c["status"] == "drone")
    silent = sum(1 for c in clips if c["status"] == "silent")
    good = [c for c in clips if c["status"] == "shaped"]
    med = sorted(c["rms"] for c in good)
    med_rms = med[len(med) // 2] if med else 0.0

    tiles = [
        ("Clips rendered", f"{len(clips)}", "every saved test clip"),
        ("Usable effects", f"{len(good)}", "shaped attack and decay"),
        ("Constant drones", f"{drones}", "no attack, masks the scene"),
        ("Median RMS, usable", f"{med_rms:.2f}", "drones sit near 0.62"),
    ]
    tile_html = "\n".join(
        f"""<div class="tile"><span class="tile-label">{l}</span>
        <span class="tile-value">{v}</span><span class="tile-sub">{s}</span></div>"""
        for l, v, s in tiles
    )
    facets = "\n".join(facet(c, i) for i, c in enumerate(clips))
    data_js = "[" + ",".join(
        "{n:%s,d:%.4f,a:[%s]}" % (
            '"' + c["name"].replace('"', '') + '"', c["duration"],
            ",".join(str(a) for a in c["amps"]))
        for c in clips) + "]"

    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(title)}</title>
<style>
:root {{ color-scheme: light dark; }}
body {{ margin:0; background: var(--plane); font-family: system-ui,-apple-system,"Segoe UI",sans-serif; }}
.viz-root {{
    color-scheme: light;
{css_vars(LIGHT)}
    background: var(--plane); color: var(--primary);
    padding: 32px clamp(16px,4vw,48px) 64px; min-height:100vh;
}}
@media (prefers-color-scheme: dark) {{
  :root:where(:not([data-theme="light"])) .viz-root {{ color-scheme: dark;
{css_vars(DARK)}
  }}
}}
:root[data-theme="dark"] .viz-root {{ color-scheme: dark;
{css_vars(DARK)}
}}
h1 {{ font-size:22px; margin:0 0 6px; letter-spacing:-0.01em; }}
.sub {{ color: var(--secondary); font-size:14px; margin:0 0 28px; max-width:70ch; line-height:1.5; }}
.toolbar {{ display:flex; gap:8px; align-items:center; margin-bottom:24px; }}
button {{ font:inherit; font-size:13px; padding:6px 12px; border-radius:8px; cursor:pointer;
  border:1px solid var(--border); background:var(--surface); color:var(--primary); }}
button[aria-pressed="true"] {{ border-color:var(--series); }}
.kpi {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:12px; margin-bottom:32px; }}
.tile {{ background:var(--surface); border:1px solid var(--border); border-radius:12px; padding:14px 16px;
  display:flex; flex-direction:column; gap:2px; }}
.tile-label {{ font-size:12px; color:var(--secondary); }}
.tile-value {{ font-size:30px; font-weight:600; line-height:1.15; }}
.tile-sub {{ font-size:11px; color:var(--muted); }}
.grid-wrap {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(340px,1fr)); gap:14px; }}
.facet {{ background:var(--surface); border:1px solid var(--border); border-radius:12px;
  margin:0; padding:12px 12px 4px; position:relative; }}
.facet-head {{ display:flex; align-items:center; justify-content:space-between; gap:8px; }}
.facet-name {{ font-size:13px; font-weight:600; }}
.facet-note {{ font-size:11px; color:var(--muted); margin:3px 0 6px; line-height:1.4; }}
.badge {{ display:inline-flex; align-items:center; gap:4px; font-size:11px; font-weight:600;
  padding:2px 7px; border-radius:999px; white-space:nowrap; }}
.badge-good {{ color:var(--good); background:color-mix(in srgb, var(--good) 12%, transparent); }}
.badge-critical {{ color:var(--critical); background:color-mix(in srgb, var(--critical) 12%, transparent); }}
.badge-warning {{ color:var(--warning); background:color-mix(in srgb, var(--warning) 16%, transparent); }}
.plot {{ width:100%; height:auto; display:block; overflow:visible; }}
.grid {{ stroke:var(--grid); stroke-width:1; }}
.axis {{ stroke:var(--axis); stroke-width:1; }}
.area {{ fill:var(--series); fill-opacity:0.10; }}
.line {{ fill:none; stroke:var(--series); stroke-width:2; stroke-linejoin:round; stroke-linecap:round; }}
.peak-dot {{ fill:var(--series); stroke:var(--surface); stroke-width:2; }}
/* Halo in the surface colour so the value stays legible where it crosses the line -- the
   same idea as the surface ring on markers, rather than a box behind the text. */
.peak-label {{ fill:var(--secondary); font-size:10px; font-weight:600;
  paint-order:stroke; stroke:var(--surface); stroke-width:3px; stroke-linejoin:round; }}
.tick {{ fill:var(--muted); font-size:9px; font-variant-numeric:tabular-nums; }}
.crosshair {{ stroke:var(--axis); stroke-width:1; }}
.tip {{ position:absolute; pointer-events:none; background:var(--surface); color:var(--primary);
  border:1px solid var(--border); border-radius:8px; padding:5px 8px; font-size:11px;
  box-shadow:0 2px 10px rgba(0,0,0,.16); white-space:nowrap; z-index:5;
  font-variant-numeric:tabular-nums; }}
table {{ border-collapse:collapse; width:100%; font-size:12px; background:var(--surface);
  border:1px solid var(--border); border-radius:12px; overflow:hidden; }}
th,td {{ padding:7px 10px; text-align:left; border-bottom:1px solid var(--grid); }}
th {{ color:var(--secondary); font-weight:600; font-size:11px; }}
td.num,th.num {{ text-align:right; font-variant-numeric:tabular-nums; }}
tr:last-child td {{ border-bottom:none; }}
h2 {{ font-size:15px; margin:36px 0 12px; }}
.foot {{ color:var(--muted); font-size:11px; margin-top:28px; line-height:1.6; max-width:80ch; }}
</style></head>
<body><div class="viz-root">
<h1>{html.escape(title)}</h1>
<p class="sub">Each panel is the <strong>controller amplitude envelope</strong> the Quest would play
&mdash; 320&nbsp;Hz, gain&nbsp;1.4, 0&ndash;255 &mdash; computed with the same code as
<code>WaveformToAmplitudes.cs</code>. A usable haptic has visible attack and decay. A
<em>drone</em> is a solid block: full amplitude throughout, nothing to feel, and it masks
everything else in the scene.</p>

<div class="toolbar">
  <button id="theme" aria-pressed="false">Toggle dark mode</button>
  <button id="tableBtn" aria-pressed="false">Show table view</button>
</div>

<div class="kpi">{tile_html}</div>
<div class="grid-wrap">{facets}</div>

<section id="tableView" hidden>
  <h2>Table view</h2>
  <table><thead><tr><th>Clip</th><th class="num">Sec</th><th class="num">Peak</th>
  <th class="num">RMS</th><th class="num">Crest</th><th class="num">Mean env</th>
  <th class="num">Perceptible</th><th>State</th></tr></thead>
  <tbody>{table_rows(clips)}</tbody></table>
</section>

<p class="foot">Peak/RMS are of the raw waveform (&minus;1..1). Crest = peak/RMS; drones sit near
1.5, usable effects above 3. &ldquo;Mean env&rdquo; is the average controller amplitude of 255 and
&ldquo;perceptible&rdquo; the share of frames at or above HapticGen&rsquo;s perceptibility floor.
Thresholds come from <code>haptic_quality.py</code>, the same module the server uses to accept or
reject a generated clip. Status is shown with an icon and a word as well as colour.</p>

<script>
const DATA = {data_js};
document.querySelectorAll('.facet').forEach(f => {{
  const svg = f.querySelector('svg'), tip = f.querySelector('.tip');
  const cross = f.querySelector('.crosshair'), hit = f.querySelector('.hit');
  const d = DATA[+f.dataset.idx];
  const PAD_L = {PAD_L}, PLOT_W = {PLOT_W}, PAD_T = {PAD_T}, PLOT_H = {PLOT_H};
  function show(evt) {{
    const r = svg.getBoundingClientRect();
    const sx = (evt.clientX - r.left) / r.width * {SVG_W};
    const frac = Math.min(1, Math.max(0, (sx - PAD_L) / PLOT_W));
    const i = Math.round(frac * (d.a.length - 1));
    const v = d.a[i];
    const x = PAD_L + frac * PLOT_W;
    cross.setAttribute('x1', x); cross.setAttribute('x2', x); cross.style.display = '';
    tip.hidden = false;
    tip.textContent = (frac * d.d).toFixed(2) + 's  ·  ' + v + '/255';
    tip.style.left = Math.min(r.width - 96, Math.max(0, (x / {SVG_W}) * r.width - 40)) + 'px';
    tip.style.top = (svg.offsetTop + (PAD_T + PLOT_H * (1 - v / 255)) / {SVG_H} * r.height - 30) + 'px';
  }}
  function hide() {{ cross.style.display = 'none'; tip.hidden = true; }}
  hit.addEventListener('pointermove', show);
  hit.addEventListener('pointerleave', hide);
  svg.setAttribute('tabindex', '0');
  svg.addEventListener('blur', hide);
}});
const root = document.documentElement, tb = document.getElementById('theme');
tb.addEventListener('click', () => {{
  const dark = root.getAttribute('data-theme') !== 'dark';
  root.setAttribute('data-theme', dark ? 'dark' : 'light');
  tb.setAttribute('aria-pressed', dark);
}});
const tv = document.getElementById('tableView'), tbtn = document.getElementById('tableBtn');
tbtn.addEventListener('click', () => {{
  tv.hidden = !tv.hidden;
  tbtn.setAttribute('aria-pressed', !tv.hidden);
  tbtn.textContent = tv.hidden ? 'Show table view' : 'Hide table view';
}});
</script>
</div></body></html>"""


def standalone_svg(clip, pal=LIGHT):
    """
    One clip as a self-contained SVG file.

    Styles are inlined with resolved hex rather than CSS variables, because a standalone .svg has
    no page around it to define them -- this way the file drops straight into slides, a paper, or
    an issue and still looks right.
    """
    line, area = sparkpath(clip["amps"])
    _, label, tone = STATUS[clip["status"]]
    tone_hex = pal[tone]
    top = 34  # room for the title band above the plot
    h = SVG_H + top
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {SVG_W} {h}"
     width="{SVG_W * 2}" height="{h * 2}" role="img"
     aria-label="{html.escape(clip['name'])}: {clip['status']}, peak {clip['peak']:.3f}, RMS {clip['rms']:.3f}">
  <style>
    text {{ font-family: system-ui,-apple-system,"Segoe UI",sans-serif; }}
    .t {{ fill:{pal['primary']}; font-size:12px; font-weight:600; }}
    .s {{ fill:{pal['muted']}; font-size:9px; font-variant-numeric:tabular-nums; }}
    .st {{ fill:{tone_hex}; font-size:10px; font-weight:600; }}
    .grid {{ stroke:{pal['grid']}; stroke-width:1; }}
    .axis {{ stroke:{pal['axis']}; stroke-width:1; }}
    .area {{ fill:{pal['series']}; fill-opacity:0.10; }}
    .line {{ fill:none; stroke:{pal['series']}; stroke-width:2; stroke-linejoin:round; stroke-linecap:round; }}
  </style>
  <rect width="100%" height="100%" fill="{pal['surface']}"/>
  <text class="t" x="{PAD_L - 24}" y="16">{html.escape(clip['name'])}</text>
  <text class="st" x="{SVG_W - 8}" y="16" text-anchor="end">{label}</text>
  <text class="s" x="{PAD_L - 24}" y="28">peak {clip['peak']:.3f} · RMS {clip['rms']:.3f} · crest {clip['crest']:.1f}</text>
  <g transform="translate(0,{top})">
    <line class="grid" x1="{PAD_L}" y1="{PAD_T + PLOT_H / 2}" x2="{PAD_L + PLOT_W}" y2="{PAD_T + PLOT_H / 2}"/>
    <line class="grid" x1="{PAD_L}" y1="{PAD_T}" x2="{PAD_L + PLOT_W}" y2="{PAD_T}"/>
    <line class="axis" x1="{PAD_L}" y1="{PAD_T + PLOT_H}" x2="{PAD_L + PLOT_W}" y2="{PAD_T + PLOT_H}"/>
    <path class="area" d="{area}"/>
    <polyline class="line" points="{line}"/>
    <text class="s" x="{PAD_L - 6}" y="{PAD_T + 4}" text-anchor="end">255</text>
    <text class="s" x="{PAD_L - 6}" y="{PAD_T + PLOT_H / 2 + 4}" text-anchor="end">128</text>
    <text class="s" x="{PAD_L - 6}" y="{PAD_T + PLOT_H + 4}" text-anchor="end">0</text>
    <text class="s" x="{PAD_L}" y="{SVG_H - 4}">0s</text>
    <text class="s" x="{PAD_L + PLOT_W}" y="{SVG_H - 4}" text-anchor="end">{clip['duration']:.2f}s</text>
  </g>
</svg>
"""


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--glob", action="append", required=True,
                   help="glob of wav files (repeatable)")
    p.add_argument("--out", default="haptic-report.html")
    p.add_argument("--title", default="HapticGen scene sweep - what the controller plays")
    p.add_argument("--svg-dir", default=None,
                   help="also write one standalone .svg per clip into this directory")
    p.add_argument("--svg-dark", action="store_true", help="render the per-clip SVGs dark")
    args = p.parse_args()

    paths = []
    for g in args.glob:
        paths.extend(sorted(glob.glob(os.path.expandvars(g))))
    if not paths:
        raise SystemExit(f"no wavs matched {args.glob}")

    clips = [analyse(p_) for p_ in paths]
    with open(args.out, "w", encoding="utf-8") as f:
        f.write(build(clips, args.title))

    print(f"{len(clips)} clips -> {args.out}")

    if args.svg_dir:
        os.makedirs(args.svg_dir, exist_ok=True)
        pal = DARK if args.svg_dark else LIGHT
        for c in clips:
            dest = os.path.join(args.svg_dir, f"{c['name']}.svg")
            with open(dest, "w", encoding="utf-8") as f:
                f.write(standalone_svg(c, pal))
        print(f"{len(clips)} standalone SVGs -> {args.svg_dir}")

    for c in clips:
        print(f"  {c['name']:28} {c['status']:7} peak={c['peak']:.3f} rms={c['rms']:.3f} "
              f"crest={c['crest']:.1f}")


if __name__ == "__main__":
    main()
