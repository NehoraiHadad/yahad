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
REPO='https://github.com/NehoraiHadad/yahad'
SITE='https://nehoraihadad.github.io/yahad/'
CREDIT='https://maklikim.co.il/l/me'  # author credit in the footer (redirects to nehoraihadad.com)
RELEASE_URL=re.compile(r'^https://github\.com/NehoraiHadad/yahad/releases/(download/[^/]+|latest/download)/[^/]+$')
for a in d.anchors:
 href=a.get('href','')
 if 'download-action' in (a.get('class') or ''):
  assert RELEASE_URL.match(href),f'Download link must be a release asset of this repo: {href}'
  continue
 if href==CREDIT:continue
 if href.startswith(REPO+'/') or href==REPO:
  assert 'releases/download' not in href and 'latest/download' not in href,f'Download link outside a download card: {href}'
  continue
 assert href.startswith('#'),f'Unexpected external action: {href}'
 assert href[1:] in d.ids,f'Missing anchor {href}'
for a in d.assets:
 if a==SITE:continue  # rel=canonical
 assert (ROOT/'dist'/a.split('?')[0]).is_file(),f'Missing asset {a}'
css=(ROOT/'dist/style.css').read_text()
film_css=(ROOT/'dist/film.css').read_text(encoding='utf-8')
for stylesheet in [css,film_css]:
 for asset in re.findall(r'url\([\'"]?([^\)\'"]+)',stylesheet):
  assert (ROOT/'dist'/asset).is_file(),asset
assets={asset.split('?',1)[0] for asset in d.assets}
# How it works: four beats (the content) beside a short film (decoration).
assert 'how' in d.classes,'How-it-works section missing'
assert {'film-scene.js','film.js','film.css'}.issubset(assets)
assert not {'how.js','how.css','demo.js'}&assets,'old explainer files must be gone'
assert d.assets.index(next(a for a in d.assets if a.startswith('film-scene.js')))<d.assets.index(next(a for a in d.assets if a.startswith('film.js'))),'scene loads before the player'
visible=re.sub(r'<!--.*?-->','',s,flags=re.S)
assert 'המחשה' not in visible,'No illustration disclaimer wording on the visible page'
section=visible.split('id="how-it-works"',1)[1].split('</section>',1)[0]
beats=re.findall(r'<li class="beat" data-at="[\d.]+" data-still="[\d.]+"><h3>(.*?)</h3><p>.+?</p></li>',section)
assert [re.sub(r'<[^>]+>','',h) for h in beats]==['נועה רוצה לכתוב לחברות','אתם בוחרים מה היא רואה','לנועה יש כניסה משלה','החברות נשארות ב־WhatsApp'],'story: the wish, the choice, her own sign-in, her friends stay on WhatsApp'
assert '<div class="film-frame" aria-hidden="true">' in section,'the film is decoration, hidden from assistive tech'
assert '<div class="film" hidden>' in section,'without JS the film area stays out'
assert section.index('class="film"')<section.index('class="beats"')
# The explainer explains the idea only; the setup (computer, QR) lives in its own section.
for word in ['QR','המחשב','מחשב']:assert word not in re.sub(r'<[^>]+>','',section),f'setup detail in the explainer: {word}'
assert 'class="primary download-action"' not in section,'No download call to action inside the explainer'
assert 'href="#how-it-works"' in s and 'class="primary" href="#download"' in s
assert '.film-live' in film_css,'the film layout applies only once film.js runs'
assert re.search(r'\.film-live \.beat:not\(\.is-active\) h3,\.film-live \.beat:not\(\.is-active\)::before\{color:var\(--muted\)\}',film_css),'inactive beats use --muted'
assert 'prefers-reduced-motion:reduce' in css
assert 'מכסה יומית נפרדת לכל ילד' in s
# Approval, not chat type, decides what reaches a child: private chats can be approved.
assert 'שיחה פרטית לא עוברת' not in visible and 'tag-lock' not in s,'Do not claim private chats are always blocked'
# Share preview: absolute OG URLs under the canonical site, image present at the declared size.
meta=dict(re.findall(r'<meta (?:property|name)="([^"]+)" content="([^"]*)"',s))
for key in ['og:title','og:description','og:url','og:image','og:image:alt','twitter:card']:assert meta.get(key),f'missing {key}'
assert meta['og:url']==SITE and meta['og:image'].startswith(SITE)
assert (ROOT/'dist'/meta['og:image'][len(SITE):]).is_file(),'og:image file missing'
assert (meta['og:image:width'],meta['og:image:height'])==('1200','630')
assert (ROOT/'dist/404.html').is_file()
# Every visible character must be inside a declared @font-face unicode-range,
# so no glyph silently falls back to a system font.
ranges=[]
for decl in re.findall(r'@font-face\{[^}]*unicode-range:([^;}]+)',css):
 for part in decl.split(','):
  lo,_,hi=part.strip()[2:].partition('-');ranges.append((int(lo,16),int(hi or lo,16)))
assert ranges,'no unicode-range in @font-face'
text=re.sub(r'<[^>]+>',' ',re.sub(r'<(script|style)(?=[ >]).*?</(?:script|style)>','',visible,flags=re.S))
import html as _html
text=_html.unescape(text)
EMOJI=lambda c:ord(c)>=0x1F000 or 0x2600<=ord(c)<=0x27BF or ord(c) in (0xFE0F,0x200D)
for js in ['film-scene.js','film.js']:
 src=(ROOT/'dist'/js).read_text(encoding='utf-8')
 text+=' '.join(re.findall(r"'([^'\\\n]*)'",src))  # the film's text and the player's labels
missing=sorted({c for c in text if not c.isspace() and not EMOJI(c) and not any(lo<=ord(c)<=hi for lo,hi in ranges)})
assert not missing,f'characters outside the bundled fonts: {[hex(ord(c)) for c in missing]}'
# One-time entrance animations were removed on purpose: content never depends on motion to appear.
assert 'reveal.js' not in s and 'hero-rise' not in css
assert '0.5.3' not in s and 'room.png' not in css and 'sun.png' not in s
art=[a for a in d.images if 'friends-at-the-park' in a['src']][0]
assert art['width']=='1536' and art['height']=='1024'
assert (ROOT/'dist/assets/friends-at-the-park.webp').stat().st_size<250000
# Per-platform download readiness. pending = disabled button, no link.
# ready = exactly one download-action link to a release asset of this repo.
def download_cards(src):
 # Balanced <li> matching: a card may contain its own nested list (install steps).
 out=[]
 for m in re.finditer(r'<li class="download-card"([^>]*)>',src):
  depth,pos=1,m.end()
  for t in re.finditer(r'<(/?)li(?=[ >])[^>]*>',src[pos:]):
   depth+=-1 if t.group(1) else 1
   if depth==0:out.append((m.group(1),src[pos:pos+t.start()]));break
 return out
def validate_download(src):
 errs=[]
 cards=download_cards(src)
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
print('PASS: Hebrew RTL, anchors/assets/CSS URLs, 4-beat explainer beside the film, approval-based wording, share preview tags, font coverage of all visible text, per-platform downloads + ready-state fixtures, illustration size budget')
print('NOTE: static checks do not establish responsive rendering, contrast, browser focus or live motion behavior.')
