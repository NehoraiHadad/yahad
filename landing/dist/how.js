/* יחד · how it works v3: scroll drives a camera around a phone and a tablet. Vanilla JS, no libraries. */
(()=>{'use strict';
const sec=document.getElementById('how-it-works');if(!sec)return;
const RM=matchMedia('(prefers-reduced-motion: reduce)');
const go=document.getElementById('watch-demo');
if(go)go.addEventListener('click',e=>{e.preventDefault();sec.scrollIntoView({behavior:RM.matches?'auto':'smooth'})});
if(RM.matches)return; // calm still (CSS) for reduced motion; same as no-JS
sec.classList.add('how-live');
const q=s=>sec.querySelector(s),qa=s=>[...sec.querySelectorAll(s)];
const scene=q('.how-scene'),cam=q('.how-cam'),phone=q('.dev-phone'),tab=q('.dev-tablet'),beam=q('.beam'),stop=q('.stop');
const caps=qa('.how-cap'),dots=qa('.how-dots i');
const pk={photo:q('.pkt-photo'),dani:q('.pkt-dani'),reply:q('.pkt-reply')};
const S={wa:q('.scr-wa'),par:q('.scr-parent'),list:q('.scr-list'),sign:q('.scr-signin'),chat:q('.scr-chat')};
const waPic=q('.wa-pic'),waReply=q('.wa-reply'),gran=q('.kid-row.gran'),pins=qa('.pin i'),btn=q('.signin-btn'),typed=q('.typed'),kidReply=q('.kid-reply'),sws=qa('.sw[data-at]');
const REPLY=Array.from('וואו! איזה יפים 😍');
const cl=(x,a=0,b=1)=>x<a?a:x>b?b:x,rp=(p,a,b)=>cl((p-a)/(b-a)),ez=t=>t*t*(3-2*t),lr=(a,b,t)=>a+(b-a)*t;
// world layouts: device centres [x,y,rotateY]; camera targets [cx,cy,w,h]; stop = where the lock sits on the line (0 phone .. 1 tablet)
const LAY={wide:{ph:[390,0,-14],tb:[-190,0,12],PH:[390,0,360,640],TB:[-190,0,620,460],OV:[100,0,1060,700],stop:.38},
 tall:{ph:[110,-275,-10],tb:[-50,375,10],PH:[110,-275,330,640],TB:[-50,375,590,440],OV:[-30,45,585,1130],stop:.57}};
// camera keyframes: [progress, target, rotateY, rotateX]
const KEYS=[[0,'PH',16,5],[.11,'PH',6,3],[.21,'OV',-9,8],[.40,'OV',6,6],[.47,'PH',-12,4],[.6,'PH',-4,3],[.67,'TB',10,6],[.8,'TB',3,4],[.87,'OV',-7,7],[.95,'PH',-8,4],[1,'PH',-3,3]];
let W=1,H=1,L=LAY.wide,top0=0,span=1,last=-1,raf=0,typedNow=null;
function measure(){const r=scene.getBoundingClientRect();W=r.width;H=r.height;L=W/H<1.05?LAY.tall:LAY.wide;
 top0=sec.getBoundingClientRect().top+scrollY;span=Math.max(1,sec.offsetHeight-innerHeight);
 const A=L.ph,B=L.tb,dx=B[0]-A[0],dy=B[1]-A[1];
 BL=Math.hypot(dx,dy);beam.style.width=BL+'px';beam.style.transform=`translate3d(${A[0]}px,${A[1]}px,-40px) rotate(${Math.atan2(dy,dx)}rad)`;
 last=-1;frame();}
function camAt(p){let i=0;while(i<KEYS.length-2&&p>KEYS[i+1][0])i++;const a=KEYS[i],b=KEYS[i+1],t=ez(rp(p,a[0],b[0])),ra=L[a[1]],rb=L[b[1]];
 return [lr(ra[0],rb[0],t),lr(ra[1],rb[1],t),lr(ra[2],rb[2],t),lr(ra[3],rb[3],t),lr(a[2],b[2],t),lr(a[3],b[3],t)];}
function put(el,x,y,z,extra,o){el.style.transform=`translate3d(${x.toFixed(1)}px,${y.toFixed(1)}px,${z}px) translate(-50%,-50%)${extra||''}`;if(o!=null)el.style.opacity=o.toFixed(3);}
const BND=[0,.10,.22,.36,.58,.70,.84,1],MAP=[0,0,.19,.305,.43,.62,.79,1];let BL=1;
function remap(P){let i=0;while(i<BND.length-2&&P>BND[i+1])i++;return lr(MAP[i],MAP[i+1],rp(P,BND[i],BND[i+1]));}
function frame(){raf=0;span=Math.max(1,sec.offsetHeight-innerHeight);const P=cl(-sec.getBoundingClientRect().top/span);if(Math.abs(P-last)<1e-5)return;last=P;const p=remap(P);
 const c0=camAt(p),cv=camAt(.3),wv=1-rp(P,.10,.15),c=c0.map((x,j)=>lr(x,cv[j],wv)),s=Math.min(W*.9/c[2],H*.9/c[3]);
 cam.style.transform=`translate3d(${W/2}px,${H/2}px,0) scale(${s.toFixed(4)}) rotateX(${c[5].toFixed(2)}deg) rotateY(${c[4].toFixed(2)}deg) translate3d(${(-c[0]).toFixed(1)}px,${(-c[1]).toFixed(1)}px,0)`;
 const bob=Math.sin(p*Math.PI*4)*6,A=L.ph,B=L.tb,at=t=>[lr(A[0],B[0],t),lr(A[1],B[1],t)];
 put(phone,A[0],A[1]+bob,0,` rotateY(${A[2]}deg) rotateX(2deg)`);put(tab,B[0],B[1]-bob,0,` rotateY(${B[2]}deg) rotateX(4deg)`);
 sec.style.setProperty('--sheen',(50+c[4]*3).toFixed(1)+'%');
 // 1. grandma's photo crosses phone -> tablet
 let t=ez(rp(p,.22,.30)),k=at(t);put(pk.photo,k[0],k[1],90,` scale(${lr(.8,1,rp(p,.21,.24)).toFixed(3)})`,rp(p,.21,.225)*(1-rp(p,.30,.315)));
 // 2. a private chat tries, stops at the lock, falls back
 t=ez(rp(p,.31,.34))*(L.stop-.13)-ez(rp(p,.38,.41))*.12;k=at(t);
 put(pk.dani,k[0],k[1],90,` translateX(${p>.34&&p<.365?(Math.sin((p-.34)*260)*5).toFixed(1):0}px)`,rp(p,.305,.315)*(1-rp(p,.39,.41)));
 k=at(L.stop);put(stop,k[0],k[1],110,` scale(${lr(.6,1,ez(rp(p,.335,.35))).toFixed(3)})`,rp(p,.335,.348)*(1-rp(p,.39,.41)));
 // 5. Noa's reply crosses tablet -> phone
 t=ez(rp(p,.88,.94));k=at(1-t);put(pk.reply,k[0],k[1],90,'',rp(p,.875,.885)*(1-rp(p,.94,.955)));
 const conn=rp(P,.02,.07)*(1-rp(P,.13,.18)),on=Math.max(conn,rp(p,.19,.22)*(1-rp(p,.44,.47)),rp(p,.85,.88)*(1-rp(p,.96,.99)));
 const g=ez(rp(P,.02,.09));beam.style.width=(BL*g).toFixed(1)+'px';beam.style.opacity=(g*(.16+.84*on)).toFixed(3);beam.style.setProperty('--flow',((p>.7?1:-1)*p*2600).toFixed(0)+'px');beam.classList.toggle('blocked',p>.335&&p<.40);
 // screens
 const w=cl(1-rp(p,.44,.47)+rp(p,.80,.83));S.wa.style.opacity=w;S.par.style.opacity=1-w;
 S.list.style.opacity=1-rp(p,.62,.645);S.sign.style.opacity=rp(p,.62,.645)*(1-rp(p,.77,.79));S.chat.style.opacity=rp(p,.77,.79);
 const pi=ez(rp(p,.03,.08));waPic.style.opacity=pi;waPic.style.transform=`scale(${lr(.9,1,pi).toFixed(3)})`;
 waReply.style.opacity=rp(p,.935,.95);gran.classList.toggle('new',p>.30);
 sws.forEach(x=>x.classList.toggle('on',p>=+x.dataset.at));
 const n=Math.floor(rp(p,.67,.72)*4.999);pins.forEach((d,i)=>d.classList.toggle('f',i<n));
 btn.classList.toggle('press',p>.735&&p<.765);
 const txt=p>.866?'':REPLY.slice(0,Math.round(rp(p,.81,.86)*REPLY.length)).join('');if(txt!==typedNow){typed.textContent=txt;typedNow=txt;}
 kidReply.style.opacity=rp(p,.866,.878);
 // captions
 let bi=0;while(bi<BND.length-2&&P>BND[bi+1])bi++;const f=bi+rp(P,BND[bi],BND[bi+1]),NC=caps.length-1;caps.forEach((el,i)=>{let d=f-(i+.5);if(i===0&&d<0)d=0;if(i===NC&&d>0)d=0;const o=cl((.5-Math.abs(d))/.14);el.style.opacity=o.toFixed(3);el.style.transform=`translateY(${(-d*26).toFixed(1)}px)`;});
 const ci=Math.min(NC,Math.floor(f));dots.forEach((d,i)=>d.classList.toggle('on',i===ci));}
addEventListener('scroll',()=>{if(!raf)raf=requestAnimationFrame(frame)},{passive:true});
addEventListener('resize',measure);if(document.fonts)document.fonts.ready.then(measure);measure();
})();
