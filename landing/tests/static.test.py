from pathlib import Path
from html.parser import HTMLParser
import re
ROOT=Path(__file__).resolve().parents[1]
class Document(HTMLParser):
 void={'area','base','br','col','embed','hr','img','input','link','meta','param','source','track','wbr'}
 def __init__(self):super().__init__();self.ids=set();self.anchors=[];self.assets=[];self.images=[];self.html={};self.classes=[];self.allclasses=[];self.stack=[]
 def handle_starttag(self,tag,attrs):
  a=dict(attrs)
  if tag=='html':self.html=a
  if 'id' in a:
   assert a['id'] not in self.ids,f'Duplicate ID: {a["id"]}'
   self.ids.add(a['id'])
  if tag=='a':self.anchors.append(a)
  if tag=='img':self.images.append(a)
  if tag in ['img','script'] and a.get('src'):self.assets.append(a['src'])
  if tag=='link' and a.get('href'):self.assets.append(a['href'])
  if a.get('id')=='how-it-works':self.classes=a['class'].split()
  classes=a.get('class','').split()
  self.allclasses.extend(classes)
  if tag not in self.void:self.stack.append((tag,classes))
 def handle_endtag(self,tag):
  assert self.stack and self.stack[-1][0]==tag,f'Mismatched {tag} at {self.getpos()}'
  self.stack.pop()
s=(ROOT/'dist/index.html').read_text();d=Document();d.feed(s)
assert not d.stack,'Unclosed HTML element'
assert d.html['lang']=='he' and d.html['dir']=='rtl'
# Download links, when wired, may point only at real release assets of this repo.
RELEASE_URL=re.compile(r'^https://github\.com/NehoraiHadad/yahad/releases/(download/[^/]+|latest/download)/[^/]+$')
for a in d.anchors:
 href=a.get('href','')
 if 'download-action' in (a.get('class') or ''):
  assert RELEASE_URL.match(href),f'Download link must be a release asset of this repo: {href}'
  continue
 assert href.startswith('#'),f'Unexpected external action: {href}'
 assert href[1:] in d.ids,f'Missing anchor {href}'
for a in d.assets:assert (ROOT/'dist'/a.split('?')[0]).is_file(),f'Missing asset {a}'
css=(ROOT/'dist/style.css').read_text()
how_css=(ROOT/'dist/how.css').read_text()
for stylesheet in [css,how_css]:
 for asset in re.findall(r'url\([\'"]?([^\)\'"]+)',stylesheet):
  assert (ROOT/'dist'/asset).is_file(),asset
assets={asset.split('?',1)[0] for asset in d.assets}
# Scroll-driven explainer: 7 pinned steps, phone + tablet in CSS 3D.
assert 'how' in d.classes,'How-it-works section missing'
assert {'how.js','how.css'}.issubset(assets)
assert 'demo.js' not in d.assets
for part in ['dev-phone','dev-tablet','scr-chat','kid-reply','wa-reply']:assert part in d.allclasses,f'no-JS end state part missing: {part}'
visible=re.sub(r'<!--.*?-->','',s,flags=re.S)
assert 'המחשה' not in visible,'No illustration disclaimer wording on the visible page'
caps=re.findall(r'<li class="how-cap" data-cap="(\d)">',visible);assert caps==['0','1','2','3','4','5','6'],caps
assert visible.index('מחברים פעם אחת')<visible.index('סבתא שולחת תמונה לקבוצה בוואטסאפ')<visible.index('יחד מעביר אותה לטאבלט של נועה')<visible.index('שיחה פרטית לא עוברת')<visible.index('אתם בוחרים אילו קבוצות')<visible.index('לנועה יש כניסה אישית משלה')<visible.index('היא עונה, וסבתא מקבלת בוואטסאפ')
explainer=visible.split('id="how-it-works"',1)[1].split('</section>',1)[0]
assert 'class="primary download-action"' not in explainer,'No download call to action inside the explainer'
assert 'href="#how-it-works"' in s and 'class="primary" href="#download"' in s
assert '.how-live' in how_css,'motion styles only apply once how.js opts in (reduced motion / no-JS keep the still)'
assert 'prefers-reduced-motion:reduce' in css
assert 'מכסת זמן יומית לכל ילד' in s
assert '0.5.3' not in s and 'room.png' not in css and 'sun.png' not in s
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
# Shipped page: Windows ready at the fixed latest-release name, Linux still pending.
assert validate_download(s)==[],validate_download(s)
assert re.findall(r'data-platform="(\w+)" data-state="ready"',s)==['windows'],'Only Windows is released'
assert 'releases/latest/download/Yahad.Setup.exe' in s and 'releases/download' not in s,'Shipped page must use the fixed link'
assert 'download=' not in s
# Fixtures: the documented activation edit, per platform and combined, must validate.
assert validate_download(activate(s,'linux',FIXTURE_URL))==[],'linux-only ready fixture rejected'
assert validate_download(s.replace(' disabled','',1)),'pending enabled button accepted'
# Negative fixtures: non-repo URL rejected; ready without a link rejected; pending with a link rejected.
assert validate_download(activate(s,'linux','https://example.com/installer.exe')),'non-repo download URL accepted'
assert validate_download(re.sub(r'data-state="pending"','data-state="ready"',s,count=1)),'ready without link accepted'
assert validate_download(s.replace('data-state="ready"','data-state="pending"',1)),'pending with link accepted'
print('PASS: Hebrew RTL, all anchors/assets/CSS URLs, 7-step scroll explainer and caption order, reduced-motion CSS, caption order, per-platform pending downloads + ready-state fixtures, quota wording, explicit illustration dimensions and <250 kB budget')
print('NOTE: static checks do not establish responsive rendering, contrast, browser focus or live motion behavior.')
