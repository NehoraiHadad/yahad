"""WCAG 2.x sRGB contrast regression for actual CSS text/background tokens.
No anti-aliasing rounding or large-text exemption: every pair must meet 4.5:1.
"""
from pathlib import Path
import re
css=(Path(__file__).resolve().parents[1]/'dist/style.css').read_text()
def declarations(selector):
 match=re.search(r'(?:^|})\s*'+re.escape(selector)+r'\{([^}]+)\}',css)
 assert match,selector
 return dict(p.split(':',1) for p in match.group(1).split(';') if ':' in p)
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
cases=[
 ('disclaimer small text',declarations('.disclaimer')['color'],vars['--paper']),
 ('composer placeholder',declarations('.compose-input')['color'],declarations('.family-demo')['background']),
 ('contact avatar initial',declarations('.contact-avatar')['color'],declarations('.contact-avatar')['background']),
 ('demo label',declarations('.product-head>span')['color'],declarations('.family-demo')['background']),
 ('parent hint',declarations('.parent-hint')['color'],declarations('.family-demo')['background']),
 ('body muted',vars['--muted'],vars['--paper']),
 ('contact caption',vars['--muted'],declarations('.approval-row')['background']),
 ('control hover',vars['--ink'],'#f0f5eb'),
 ('primary label','#ffffff',vars['--green']),
 ('group avatar',declarations('.contact-avatar.g')['color'],declarations('.contact-avatar.g')['background']),
 ('group reply sender',declarations('.demo-reply .sender')['color'],declarations('.demo-reply')['background'])]
for name,f,b in cases:
 r=ratio(f,b);print(f'{name}: {value(f)} / {value(b)} = {r:.3f}:1');assert r>=4.5,(name,r)
assert declarations('.compose-input')['color']=='var(--muted)'
assert declarations('.disclaimer')['color']=='var(--muted)'
assert '#667d6e' not in css and '#735e4c' not in css
print('PASS: all text/background pairs exceed 4.5:1; three failing selector/color pairs corrected.')
