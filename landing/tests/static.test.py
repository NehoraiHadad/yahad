from pathlib import Path
from html.parser import HTMLParser
import re
ROOT=Path(__file__).resolve().parents[1]
class Document(HTMLParser):
 def __init__(self):super().__init__();self.ids=set();self.anchors=[];self.assets=[];self.images=[];self.html={};self.classes=[]
 def handle_starttag(self,tag,attrs):
  a=dict(attrs)
  if tag=='html':self.html=a
  if 'id' in a:self.ids.add(a['id'])
  if tag=='a':self.anchors.append(a.get('href',''))
  if tag=='img':self.images.append(a)
  if tag in ['img','script'] and a.get('src'):self.assets.append(a['src'])
  if tag=='link' and a.get('href'):self.assets.append(a['href'])
  if a.get('id')=='family-demo':self.classes=a['class'].split()
s=(ROOT/'dist/index.html').read_text();d=Document();d.feed(s)
assert d.html['lang']=='he' and d.html['dir']=='rtl'
for a in d.anchors:
 assert a.startswith('#'),f'Unexpected external action: {a}'
 assert a[1:] in d.ids,f'Missing anchor {a}'
for a in d.assets:assert (ROOT/'dist'/a.split('?')[0]).is_file(),f'Missing asset {a}'
css=(ROOT/'dist/style.css').read_text()
for asset in re.findall(r'url\([\'"]?([^\)\'\"]+)',css):assert (ROOT/'dist'/asset).is_file(),asset
for state in ['s-check','s-arrive','s-msg1','s-reply','s-compose']:assert state in d.classes,'Static/no-JS complete state missing'
assert 'prefers-reduced-motion:reduce' in css
assert 'אין הורדה זמינה כרגע' in s and 'download=' not in s
assert 'מכסת זמן יומית לכל ילד' in s
assert '0.5.3' not in s and 'room.png' not in css and 'sun.png' not in s
assert s.index('parent-context')<s.index('child-context')
art=[a for a in d.images if 'family-moment' in a['src']][0]
assert art['width']=='1536' and art['height']=='1024'
assert (ROOT/'dist/assets/family-moment.webp').stat().st_size<250000
print('PASS: Hebrew RTL, all anchors/assets/CSS URLs, no-JS complete demo, reduced-motion CSS, parent-before-child DOM, no unavailable download, quota wording, explicit illustration dimensions and <250 kB budget')
print('NOTE: static checks do not establish responsive rendering, contrast, browser focus or live motion behavior.')
