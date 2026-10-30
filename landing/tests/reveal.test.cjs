const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const source=fs.readFileSync('dist/reveal.js','utf8');
function setup(options={}){
 const el=()=>{const classes=new Set();return {classes,classList:{add:x=>classes.add(x),remove:x=>classes.delete(x)},contains(target){return this.child===target}}};
 const root=el(),nodes=[el(),el(),el()],child={};nodes[1].child=child;
 let callback;const events={},winEvents={},observed=new Set();
 const media={matches:!!options.reduced,addEventListener(k,fn){this.change=fn}};
 class IO{constructor(fn){if(options.constructorFails)throw Error('unsupported');callback=fn}observe(n){if(options.observeFails)throw Error('observer failure');observed.add(n)}unobserve(n){observed.delete(n)}disconnect(){observed.clear()}}
 const document={documentElement:root,querySelectorAll:()=>nodes,activeElement:options.focus?child:null,getElementById:id=>id==='faq'?nodes[1]:null,addEventListener:(k,f)=>events[k]=f};
 const window={matchMedia:()=>media,location:{hash:options.hash||''},addEventListener:(k,f)=>winEvents[k]=f};if(!options.noIO)window.IntersectionObserver=IO;
 vm.runInNewContext(source,{window,document,IntersectionObserver:IO});
 return {root,nodes,child,media,events,winEvents,observed,enter:n=>callback([{isIntersecting:true,target:n}])};
}
for(const opts of [{noIO:true},{reduced:true},{constructorFails:true},{observeFails:true}]){const h=setup(opts);assert(!h.root.classes.has('js-reveal'),'unavailable setup must stay visible')}
let h=setup();assert(h.root.classes.has('js-reveal'));h.enter(h.nodes[0]);assert(h.nodes[0].classes.has('is-visible'));assert(!h.observed.has(h.nodes[0]));
h.events.focusin({target:h.child});assert(h.nodes[1].classes.has('is-visible'),'keyboard focus reveals container');
h.media.matches=true;h.media.change();assert(!h.root.classes.has('js-reveal'));assert.equal(h.observed.size,0);assert(h.nodes.every(n=>n.classes.has('is-visible')));
h.media.matches=false;h.media.change();assert(!h.root.classes.has('js-reveal'),'preference toggling must not rehide content');
h=setup({focus:true});assert(h.nodes[1].classes.has('is-visible'),'already focused content remains visible');
h=setup({hash:'#faq'});assert(h.nodes[1].classes.has('is-visible'),'fragment target reveals before scroll');
h=setup({hash:'#%invalid'});assert(h.root.classes.has('js-reveal'),'bad fragment does not crash setup');
const css=fs.readFileSync('dist/style.css','utf8');assert(css.includes('.reveal:focus-within'));assert(css.includes('.reveal:target'));assert(css.includes('.primary:hover img{transform:none}'));
console.log('PASS: optional IO/reduced-motion baseline, constructor/observe fail-open, viewport reveal, keyboard/current focus, fragment target, malformed fragment, preference-change fail-open, CSS focus/reduced-motion guards');
