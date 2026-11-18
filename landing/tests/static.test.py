from pathlib import Path
from html.parser import HTMLParser
import re
ROOT=Path(__file__).resolve().parents[1]
class Document(HTMLParser):
 void={'area','base','br','col','embed','hr','img','input','link','meta','param','source','track','wbr'}
 def __init__(self):super().__init__();self.ids=set();self.anchors=[];self.assets=[];self.images=[];self.html={};self.elements=[];self.stack=[]
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
  self.elements.append((tag,a,list(self.stack)))
  if tag not in self.void:self.stack.append((tag,a))
 def handle_endtag(self,tag):
  assert self.stack and self.stack[-1][0]==tag,f'Mismatched {tag} at {self.getpos()}'
  self.stack.pop()
s=(ROOT/'dist/index.html').read_text();d=Document();d.feed(s)
assert not d.stack,'Unclosed HTML element'
assert d.html['lang']=='he' and d.html['dir']=='rtl'
# The only installer link must stay visible and point at the repository's current Windows release.
REPO='https://github.com/NehoraiHadad/yahad'
SITE='https://nehoraihadad.github.io/yahad/'
CREDIT='https://maklikim.co.il/l/me'  # author credit in the footer (redirects to nehoraihadad.com)
INSTALLER=REPO+'/releases/latest/download/Yahad.Setup.exe'
downloads=[(a,ancestors) for tag,a,ancestors in d.elements if tag=='a' and 'download-action' in a.get('class','').split()]
assert len(downloads)==1,'Expected one installer link'
installer,parents=downloads[0]
assert installer['href']==INSTALLER and installer['id']=='download'
assert ('section', {'class':'opening shell', 'aria-labelledby':'hero-title'}) in parents
assert not any(tag=='details' or 'hidden' in a for tag,a in parents),'Installer must be visible on arrival'
for a in d.anchors:
 href=a.get('href','')
 if 'download-action' in (a.get('class') or ''):
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
# Header links reach the film, both technical subsections, and the installer.
nav_links=[a['href'] for tag,a,parents in d.elements if tag=='a' and any(parent_tag=='nav' for parent_tag,_ in parents)]
assert nav_links==['#how-it-works','#home','#faq','#download']
assert 'navigation.js' in assets
# The opening and film are visible, with the supporting information in a single closed disclosure.
assert {'film-scene.js','film.js','film.css'}.issubset(assets)
assert d.assets.index(next(a for a in d.assets if a.startswith('film-scene.js')))<d.assets.index(next(a for a in d.assets if a.startswith('film.js'))),'scene loads before the player'
visible=re.sub(r'<!--.*?-->','',s,flags=re.S)
main_children=[(tag,a) for tag,a,ancestors in d.elements if ancestors and ancestors[-1][0]=='main']
assert [tag for tag,_ in main_children]==['section','section','details']
assert main_children[1][1].get('id')=='how-it-works'
technical=main_children[2][1]
assert technical.get('id')=='technical' and 'open' not in technical,'Technical details must be closed initially'
for name in ['home','setup','risk','faq']:
 assert any(a.get('id')==name and ('details',technical) in parents for _,a,parents in d.elements),name
assert '<div class="film-frame" aria-hidden="true">' in s
assert '<div class="film" hidden>' in s
assert '<p class="film-description">' in s,'Film must have a text description'
assert 'class="beats"' not in s,'Keep the homepage free of duplicate chapter text'
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
print('PASS: Hebrew RTL, focused homepage, visible installer, closed technical information, film description, anchors/assets, share metadata and font coverage')
