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
how_css=(ROOT/'dist/how.css').read_text()
for stylesheet in [css,how_css]:
 for asset in re.findall(r'url\([\'"]?([^\)\'"]+)',stylesheet):
  assert (ROOT/'dist'/asset).is_file(),asset
assets={asset.split('?',1)[0] for asset in d.assets}
# Scroll-driven explainer: 7 pinned steps, phone + tablet in CSS 3D.
assert 'how' in d.classes,'How-it-works section missing'
assert {'how.js','how.css'}.issubset(assets)
assert 'demo.js' not in d.assets
for part in ['how-stage','how-cam','dev-phone','dev-tablet','scr-chat','kid-reply','wa-reply']:assert part in d.allclasses,f'explainer part missing: {part}'
visible=re.sub(r'<!--.*?-->','',s,flags=re.S)
assert 'המחשה' not in visible,'No illustration disclaimer wording on the visible page'
section=visible.split('id="how-it-works"',1)[1].split('</section>',1)[0]
# The steps are the content (real headings, readable without JS); the stage is decoration.
steps=re.findall(r'<li class="how-step" data-step="(\d)"><h3>(.*?)</h3><p>.+?</p></li>',section)
assert [n for n,_ in steps]==[str(i) for i in range(1,8)],steps
assert [re.sub(r'<[^>]+>','',h) for _,h in steps]==['מחברים את WhatsApp פעם אחת','בוחרים מה נועה רואה','לנועה יש כניסה משלה','סבתא שולחת תמונה לקבוצה','והתמונה מגיעה לנועה','מה שלא אישרתם, לא עובר','נועה עונה, וסבתא מקבלת בוואטסאפ'],'story order: connect, choose, sign in, then the message flow'
assert '<div class="how-stage" aria-hidden="true">' in section,'stage must be hidden from assistive tech'
# On narrow screens the stage is position:sticky in block flow, so it must come before the steps.
assert section.index('class="how-stage"')<section.index('class="how-steps"'),'stage must precede the steps'
how_js=(ROOT/'dist/how.js').read_text(encoding='utf-8')
target=re.search(r'const TARGET = \{([^}]*)\}',how_js).group(1)
assert sorted(re.findall(r'(\d+):',target))==[n for n,_ in steps],'how.js TARGET must name every step'
# how.js aims the camera at world rectangles; they must match the device boxes in how.css.
def rule(sel):
 # Exact selector at the start of a rule (top level: preceded by start, newline or '}').
 m=re.search(r'(?:^|[}\n])'+re.escape(sel)+r'\{([^}]*)\}',how_css);assert m,sel
 return dict(re.findall(r'([\w-]+):(-?\d+)(?:px)?(?=;|$)',m.group(1)))
size={k:dict(zip(('width','height'),re.search(r'\.dev-'+k+r'\{width:(\d+)px;height:(\d+)px',how_css).groups())) for k in ('phone','tablet')}
assert "dataset.world" in how_js,'how.js must choose the world (data-world) that how.css positions from'
for layout,prefix in (('wide',''),('tall','.how[data-world="tall"] ')):
 world=re.search(layout+r': \{([^}]*)\}',how_js).group(1)
 rects={k:[int(x) for x in v.split(',')] for k,v in re.findall(r'(\w+): \[([^\]]+)\]',world)}
 cam=rule(prefix+'.how-cam')
 assert rects['OV']==[0,0,int(cam['width']),int(cam['height'])],(layout,'OV',rects['OV'],cam)
 for key,dev in (('PH','phone'),('TB','tablet')):
  pos=rule(prefix+'.dev-'+dev)
  assert rects[key]==[int(pos['left']),int(pos['top']),int(size[dev]['width']),int(size[dev]['height'])],(layout,key,rects[key],pos)
explainer=visible.split('id="how-it-works"',1)[1].split('</section>',1)[0]
assert 'class="primary download-action"' not in explainer,'No download call to action inside the explainer'
assert 'href="#how-it-works"' in s and 'class="primary" href="#download"' in s
assert '.how-live' in how_css,'motion styles only apply once how.js opts in (reduced motion / no-JS keep the still)'
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
missing=sorted({c for c in text if not c.isspace() and not EMOJI(c) and not any(lo<=ord(c)<=hi for lo,hi in ranges)})
assert not missing,f'characters outside the bundled fonts: {[hex(ord(c)) for c in missing]}'
# One-time entrance animations were removed on purpose: content never depends on motion to appear.
assert 'reveal.js' not in s and 'hero-rise' not in css
assert '0.5.3' not in s and 'room.png' not in css and 'sun.png' not in s
art=[a for a in d.images if 'family-moment' in a['src']][0]
assert art['width']=='1536' and art['height']=='1024'
assert (ROOT/'dist/assets/family-moment.webp').stat().st_size<250000
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
print('PASS: Hebrew RTL, anchors/assets/CSS URLs, 7-step explainer order, stage/world coordinates in sync, approval-based wording, share preview tags, font coverage of all visible text, per-platform downloads + ready-state fixtures, illustration size budget')
print('NOTE: static checks do not establish responsive rendering, contrast, browser focus or live motion behavior.')
