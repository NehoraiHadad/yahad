"""WCAG 2.x sRGB contrast regression for actual CSS text/background tokens.
No anti-aliasing rounding or large-text exemption: every pair must meet 4.5:1.
"""
from pathlib import Path
import re
css=(Path(__file__).resolve().parents[1]/'dist/style.css').read_text()
how_css=(Path(__file__).resolve().parents[1]/'dist/how.css').read_text()
def declarations(selector, stylesheet=css):
 match=re.search(r'(?:^|}|\*/)\s*'+re.escape(selector)+r'\s*\{([^}]+)\}',stylesheet)
 assert match,selector
 return dict((k.strip(),v.strip()) for k,v in (p.split(':',1) for p in match.group(1).split(';') if ':' in p))
vars=declarations(':root')
def value(s):
 if s.startswith('var('):return vars[s[4:-1]]
 if len(s)==4:return '#'+''.join(c*2 for c in s[1:])
 return s
def lum(s):
 h=value(s);cs=[int(h[i:i+2],16)/255 for i in (1,3,5)]
 return sum((c/12.92 if c<=.04045 else ((c+.055)/1.055)**2.4)*w for c,w in zip(cs,[.2126,.7152,.0722]))
def ratio(f,b):
 lo,hi=sorted((lum(f),lum(b)));return (hi+.05)/(lo+.05)
how=declarations('.how',how_css)
cases=[
 ('disclaimer small text',declarations('.disclaimer')['color'],vars['--paper']),
 ('body muted',vars['--muted'],vars['--paper']),
 ('primary label','#ffffff',vars['--green']),
 # scroll explainer: tokens on white and on the darkest edge of its radial background
 ('explainer title',how['--ink'],'#ffffff'),
 ('explainer copy',how['--mut'],'#ffffff'),
 ('explainer eyebrow',how['--g'],'#ffffff'),
 ('explainer copy on edge',how['--mut'],'#e2e8e6'),
 ('explainer eyebrow on edge',how['--g'],'#e2e8e6'),
]
for name,f,b in cases:
 r=ratio(f,b);print(f'{name}: {value(f)} / {value(b)} = {r:.3f}:1');assert r>=4.5,(name,r)
assert declarations('.disclaimer')['color']=='var(--muted)'
print('PASS: all checked text/background pairs meet 4.5:1.')
