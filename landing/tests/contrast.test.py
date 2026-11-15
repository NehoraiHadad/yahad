"""WCAG 2.x sRGB contrast regression for the text/background token pairs the
page actually uses. No large-text exemption: every pair must meet 4.5:1."""
from pathlib import Path
import re
DIST=Path(__file__).resolve().parents[1]/'dist'
css=(DIST/'style.css').read_text(encoding='utf-8')
film_css=(DIST/'film.css').read_text(encoding='utf-8')
def declarations(selector,stylesheet):
 m=re.search(r'(?:^|}|\*/)\s*'+re.escape(selector)+r'\s*\{([^}]+)\}',stylesheet)
 assert m,selector
 return {k.strip():v.strip() for k,v in (p.split(':',1) for p in m.group(1).split(';') if ':' in p)}
root=declarations(':root',css)
assert re.search(r'\.film-live \.beat:not\(\.is-active\) h3,\.film-live \.beat:not\(\.is-active\)::before\{color:var\(--muted\)\}',film_css),'inactive beats must use --muted'
def hexval(v,scope=root):
 v=v.strip()
 if v.startswith('var('):return hexval(scope[v[4:-1]],scope)
 if len(v)==4:return '#'+''.join(c*2 for c in v[1:])
 return v
def lum(v):
 h=hexval(v);cs=[int(h[i:i+2],16)/255 for i in (1,3,5)]
 return sum((c/12.92 if c<=.04045 else ((c+.055)/1.055)**2.4)*w for c,w in zip(cs,[.2126,.7152,.0722]))
def ratio(a,b):
 lo,hi=sorted((lum(a),lum(b)));return (hi+.05)/(lo+.05)
disabled=declarations('.primary[disabled]',css)
cases=[
 ('body text','var(--ink)','var(--paper)'),
 ('muted text on paper','var(--muted)','var(--paper)'),
 ('muted text on surface','var(--muted)','var(--surface)'),
 ('pass headings and links on paper','var(--pass)','var(--paper)'),
 ('pass hover on paper','var(--pass-strong)','var(--paper)'),
 ('button label','#ffffff','var(--pass)'),
 ('button label hover','#ffffff','var(--pass-strong)'),
 ('disabled button label',disabled['color'],disabled['background']),
 ('held heading on paper','var(--held-ink)','var(--paper)'),
 ('risk heading','var(--held-ink)','var(--held-wash)'),
 ('risk text','var(--ink)','var(--held-wash)'),
 ('quota numbers','var(--pass)','var(--surface)'),
 ('film button label','var(--pass)','var(--surface)'),
 ('film button hover','var(--pass-strong)','var(--surface)'),
 ('active beat number on paper','var(--pass)','var(--paper)'),
 ('beat heading hover on paper','var(--pass-strong)','var(--paper)'),
]
for name,f,b in cases:
 r=ratio(f,b);print(f'{name}: {hexval(f)} / {hexval(b)} = {r:.2f}:1');assert r>=4.5,(name,r)
print('PASS: all checked text/background pairs meet 4.5:1.')
