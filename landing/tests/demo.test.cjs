const vm=require('node:vm'),fs=require('node:fs'),assert=require('node:assert/strict');
function setup(reduce=false){
 let now=0,id=0,observe;const timers=new Map(),frames=new Map(),events={};
 const animations=[{playState:'running',pause(){this.playState='paused'},play(){this.playState='running'},cancel(){this.playState='idle'}}];
 function el(){const c=new Set();return{dataset:{},style:{setProperty(k,v){this[k]=v}},attrs:{},offsetWidth:40,offsetHeight:30,offsetTop:10,clientLeft:0,clientTop:0,hidden:true,textContent:'',classList:{add:(...xs)=>xs.forEach(x=>c.add(x)),remove:(...xs)=>xs.forEach(x=>c.delete(x)),toggle:(x,b)=>b?c.add(x):c.delete(x),contains:x=>c.has(x)},appendChild(){},querySelector(){return el()},setAttribute(k,v){this.attrs[k]=v},addEventListener(k,f){this[k]=f},getBoundingClientRect:()=>({left:10,top:10,width:100,height:30}),getTotalLength:()=>100,getAnimations:()=>animations};}
 const root=el(),control=el(),announce=el(),nodes={};const steps=[0,1,2].map(i=>Object.assign(el(),{dataset:{step:String(i)}}));root.querySelectorAll=()=>steps;root.querySelector=s=>nodes[s]||(nodes[s]=Object.assign(el(),{offsetParent:root}));
 const media={matches:reduce,addEventListener(k,f){this.change=f}};
 const doc={createElement:()=>el(),hidden:false,getElementById:k=>({'family-demo':root,'motion-toggle':control,'demo-announcement':announce}[k]),addEventListener:(k,f)=>events[k]=f};
 class IO{constructor(f){observe=f}observe(){}}
 const win={matchMedia:()=>media,IntersectionObserver:IO,addEventListener:(k,f)=>events[k]=f};
 vm.runInNewContext(fs.readFileSync('dist/demo.js','utf8'),{window:win,document:doc,IntersectionObserver:IO,performance:{now:()=>now},setTimeout:(f,ms)=>{timers.set(++id,{f,t:now+ms});return id},clearTimeout:i=>timers.delete(i),requestAnimationFrame:f=>{frames.set(++id,f);return id},cancelAnimationFrame:i=>frames.delete(i)});
 const frame=()=>{const list=[...frames.values()];frames.clear();list.forEach(f=>f(now));};
 return{root,control,announce,steps,media,doc,events,animations,timers,frames,nodes,frame,enter:()=>observe([{isIntersecting:true}]),leave:()=>observe([{isIntersecting:false}]),advance(ms){now+=ms;for(const [k,v]of [...timers])if(v.t<=now){timers.delete(k);v.f()}frame()}};
}
let h=setup();assert.equal(h.root.dataset.stage,'2');h.enter();h.frame();h.advance(1700);assert.equal(h.root.dataset.stage,'1');h.control.click();assert.equal(h.timers.size,0);assert.equal(h.frames.size,0);assert.equal(h.animations[0].playState,'paused');h.leave();h.enter();assert.equal(h.timers.size,0,'manual pause persists');h.control.click();assert.ok(h.timers.size);h.advance(6000);assert.equal(h.root.dataset.stage,'2');assert.equal(h.timers.size,0);assert.equal(h.frames.size,0);
h.control.click();h.frame();h.advance(1200);h.media.matches=true;h.media.change();assert.equal(h.timers.size,0);assert.equal(h.frames.size,0);assert.equal(h.root.dataset.stage,'2');assert.match(h.control.attrs['aria-label'],/לשלב הבא/);h.advance(10000);assert.equal(h.root.dataset.stage,'2');h.control.click();assert.equal(h.root.dataset.stage,'0');assert.equal(h.timers.size,0);
h=setup();h.enter();h.frame();h.advance(2000);h.events.resize();assert.ok(h.timers.size);h.frame();assert.equal(h.nodes['.travel-chip'].style.visibility,'visible');h.leave();assert.equal(h.timers.size,0);h.enter();assert.ok(h.timers.size);h.doc.hidden=true;h.events.visibilitychange();assert.equal(h.frames.size,0);h.doc.hidden=false;h.events.visibilitychange();assert.ok(h.frames.size);h.steps[1].click();assert.equal(h.root.dataset.stage,'1');assert.equal(h.timers.size,0);
h=setup(true);h.enter();assert.equal(h.timers.size,0);assert.match(h.control.attrs['aria-label'],/לשלב הבא/);h.control.click();assert.equal(h.root.dataset.stage,'0');
console.log('PASS: finite timeline, CSS pause/resume, manual pause persistence, offscreen/hidden pause, reduced-motion timer cancellation + labels, resize flow continuity, static/manual states');

h=setup();h.media.matches=true;h.media.change();h.media.matches=false;h.media.change();h.enter();assert.equal(h.timers.size,0,'preference change prevents surprise autoplay');
console.log('PASS: v2 labels, visibility, preference-change autoplay guard');
