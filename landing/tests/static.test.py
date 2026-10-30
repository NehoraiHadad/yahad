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
  if tag=='a':self.anchors.append(a)
  if tag=='img':self.images.append(a)
  if tag in ['img','script'] and a.get('src'):self.assets.append(a['src'])
  if tag=='link' and a.get('href'):self.assets.append(a['href'])
  if a.get('id')=='family-demo':self.classes=a['class'].split()
s=(ROOT/'dist/index.html').read_text();d=Document();d.feed(s)
assert d.html['lang']=='he' and d.html['dir']=='rtl'
# Download links, when wired, may point only at real release assets of this repo.
RELEASE_URL=re.compile(r'^https://github\.com/NehoraiHadad/yahad/releases/download/[^/]+/[^/]+$')
for a in d.anchors:
 href=a.get('href','')
 if 'download-action' in (a.get('class') or ''):
  assert RELEASE_URL.match(href),f'Download link must be a release asset of this repo: {href}'
  continue
 assert href.startswith('#'),f'Unexpected external action: {href}'
 assert href[1:] in d.ids,f'Missing anchor {href}'
for a in d.assets:assert (ROOT/'dist'/a.split('?')[0]).is_file(),f'Missing asset {a}'
css=(ROOT/'dist/style.css').read_text()
for asset in re.findall(r'url\([\'"]?([^\)\'\"]+)',css):assert (ROOT/'dist'/asset).is_file(),asset
for state in ['s-check','s-arrive','s-msg1','s-reply','s-compose']:assert state in d.classes,'Static/no-JS complete state missing'
assert 'prefers-reduced-motion:reduce' in css
assert 'מכסת זמן יומית לכל ילד' in s
assert '0.5.3' not in s and 'room.png' not in css and 'sun.png' not in s
assert s.index('parent-context')<s.index('child-context')
art=[a for a in d.images if 'family-moment' in a['src']][0]
assert art['width']=='1536' and art['height']=='1024'
assert (ROOT/'dist/assets/family-moment.webp').stat().st_size<250000
# Per-platform download readiness. pending = disabled button, no link.
# ready = exactly one download-action link to a release asset of this repo.
def validate_download(src):
 errs=[]
 cards=re.findall(r'<li class="download-card"([^>]*)>(.*?)</li>',src,re.S)
 platforms=[]
 for attrstr,body in cards:
  attrs=dict(re.findall(r'([\w-]+)="([^"]*)"',attrstr))
  plat=attrs.get('data-platform');state=attrs.get('data-state');platforms.append(plat)
  if state not in ('pending','ready'):errs.append(f'{plat}: unknown data-state {state}');continue
  links=[]
  for am in re.finditer(r'<a\b([^>]*)>(.*?)</a>',body,re.S):
   aattrs=dict(re.findall(r'([\w-]+)="([^"]*)"',am.group(1)))
   if 'download-action' in (aattrs.get('class') or ''):links.append(aattrs.get('href',''))
  if state=='pending':
   if '<a' in body:errs.append(f'{plat}: pending card carries a link')
   buttons=re.findall(r'<button\b([^>]*)>',body)
   if len(buttons)!=1 or not re.search(r'\bdisabled(?:\s|=|$)',buttons[0]):errs.append(f'{plat}: pending card needs one disabled button')
  else:
   if len(links)!=1:errs.append(f'{plat}: ready card needs exactly one download link, found {len(links)}')
   elif not RELEASE_URL.match(links[0]):errs.append(f'{plat}: link is not a release asset of this repo: {links[0]}')
 for plat in ('windows','linux'):
  if platforms.count(plat)!=1:errs.append(f'expected exactly one download card for {plat}')
 if set(platforms)-{'windows','linux'}:errs.append('unexpected platform')
 return errs
def activate(src,platform,url):
 m=re.search(r'<li class="download-card"([^>]*data-platform="%s"[^>]*)>(.*?)</li>'%platform,src,re.S)
 assert m,f'no card for {platform}'
 head,body=m.group(1),m.group(2)
 head=head.replace('data-state="pending"','data-state="ready"')
 body=re.sub(r'<button class="primary download-action"[^>]*>(.*?)</button>',r'<a class="primary download-action" href="%s">\1</a>'%url,body)
 return src[:m.start()]+'<li class="download-card"'+head+'>'+body+'</li>'+src[m.end():]
FIXTURE_URL='https://github.com/NehoraiHadad/yahad/releases/download/test-fixture/installer-file'
# Shipped page: both platforms pending, no download link anywhere, no release URL shipped.
assert validate_download(s)==[],validate_download(s)
assert not re.search(r'data-state="ready"',s),'Shipped page marks a download ready'
assert 'releases/download' not in s,'Shipped page carries a release URL'
assert 'אין הורדה זמינה כרגע' in s and 'download=' not in s
# Fixtures: the documented activation edit, per platform and combined, must validate.
assert validate_download(activate(s,'windows',FIXTURE_URL))==[],'windows-only ready fixture rejected'
assert validate_download(activate(s,'linux',FIXTURE_URL))==[],'linux-only ready fixture rejected'
assert validate_download(activate(activate(s,'windows',FIXTURE_URL),'linux',FIXTURE_URL))==[],'both-ready fixture rejected'
assert validate_download(s.replace(' disabled','',1)),'pending enabled button accepted'
# Negative fixtures: non-repo URL rejected; ready without a link rejected; pending with a link rejected.
assert validate_download(activate(s,'windows','https://example.com/installer.exe')),'non-repo download URL accepted'
assert validate_download(re.sub(r'data-state="pending"','data-state="ready"',s,count=1)),'ready without link accepted'
assert validate_download(activate(s,'windows',FIXTURE_URL).replace('data-state="ready"','data-state="pending"',1)),'pending with link accepted'
print('PASS: Hebrew RTL, all anchors/assets/CSS URLs, no-JS complete demo, reduced-motion CSS, parent-before-child DOM, per-platform pending downloads + ready-state fixtures, quota wording, explicit illustration dimensions and <250 kB budget')
print('NOTE: static checks do not establish responsive rendering, contrast, browser focus or live motion behavior.')
