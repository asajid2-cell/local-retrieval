'use strict';
/* The take/release trial: what does the page actually receive while it switches, and when?
 *
 * The report is "taking the grid glitches into [scrollback unavailable] and giving it back takes a while".
 * That is a statement about the frames between the click and the settled paint, so this records every
 * inbound frame with its arrival time and every write the pane was given, then taps once, waits, taps again.
 * Read-only apart from the two taps the user themselves performs.
 * Usage: MUX_SESSION=<name> node take_probe.cjs
 */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const UA='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE=process.env.MUX_BASE||'https://harmonizerlabs.cc/multiplex';
const S=process.env.MUX_SESSION||'tab-2ef4';
const COOKIE=(process.env.MUX_COOKIE||'').trim();
const pairs=COOKIE.split(';').map(p=>p.trim()).filter(Boolean).map(p=>{const i=p.indexOf('=');return{name:p.slice(0,i),value:p.slice(i+1)}});
const T=()=>Date.now();
(async()=>{
  if(!pairs.length){console.error('MUX_COOKIE missing');process.exit(2);}
  const b=await chromium.launch({headless:true,args:['--no-proxy-server']});
  const c=await b.newContext({viewport:{width:1440,height:900},locale:'en-US',userAgent:UA});
  await c.addCookies(pairs.map(x=>({...x,domain:'harmonizerlabs.cc',path:'/',httpOnly:true,secure:true,sameSite:'Lax'})));
  const p=await c.newPage();
  await p.addInitScript(()=>{
    const L=window.__tp={frames:[],writes:[],t0:0,marks:[]};
    const dec=new TextDecoder('utf-8');
    const hook=()=>{
      const O=window.WebSocket;
      if(!O||O.__tp){setTimeout(hook,5);return;}
      O.__tp=1;
      window.WebSocket=function(u,pr){
        const ws=pr?new O(u,pr):new O(u);
        ws.addEventListener('message',ev=>{
          L.frames.push({t:performance.now(),k:typeof ev.data==='string'?ev.data.slice(0,1):'[bin]',
            n:typeof ev.data==='string'?ev.data.length:(ev.data.byteLength||0),
            head:typeof ev.data==='string'?ev.data.slice(0,60):''});
        });
        ws.addEventListener('open',()=>L.frames.push({t:performance.now(),k:'OPEN',n:0,head:''}));
        ws.addEventListener('close',e=>L.frames.push({t:performance.now(),k:'CLOSE',n:0,head:String(e.code)+' '+String(e.reason||'')}));
        return ws;
      };
      window.WebSocket.prototype=O.prototype;
    };
    hook();
    const boot=()=>{
      if(typeof term==='undefined'||!term||!term.write){setTimeout(boot,5);return;}
      const ow=term.write.bind(term);
      term.write=(d,cb)=>{
        const s=typeof d==='string'?d:dec.decode(d);
        L.writes.push({t:performance.now(),n:s.length,tag:s.slice(0,40)});
        return ow(d,cb);
      };
    };
    boot();
  });
  await p.goto(`${BASE}/?s=${encodeURIComponent(S)}`,{waitUntil:'domcontentloaded',timeout:60000});
  await p.waitForFunction(()=>{try{return typeof term!=='undefined'&&term.cols>1&&current}catch(e){return false}},null,{timeout:30000});
  await assertSession(p,S);
  await p.waitForTimeout(2500);
  const snap=async(label)=>{
    const st=await p.evaluate(()=>({
      chip:((document.querySelector('#sizebtn')||{}).textContent||'').replace(/\s+/g,' ').trim(),
      owner:(()=>{try{return sizeGridOwner}catch(e){return null}})(),
      pinned:(()=>{try{return sizePinned}catch(e){return null}})(),
      mine:(()=>{try{return sizeMine}catch(e){return null}})(),
      busy:(()=>{try{return _gridBusy}catch(e){return null}})(),
      cols:term.cols,rows:term.rows,font:term.options.fontSize,
      lastFrames:(window.__tp.frames).slice(-6).map(f=>`${Math.round(f.t)}:${f.k}${f.n?':'+f.n:''}`),
    }));
    console.log(label, JSON.stringify(st));
  };
  await snap('initial');
  await p.evaluate(()=>{window.__tp.marks.push({t:performance.now(),m:'tap1'});});
  await p.evaluate(()=>document.querySelector('#sizebtn').click());
  for(let i=1;i<=8;i++){ await p.waitForTimeout(300); await snap('take+'+i*300+'ms'); }
  await p.evaluate(()=>{window.__tp.marks.push({t:performance.now(),m:'tap2'});});
  await p.evaluate(()=>document.querySelector('#sizebtn').click());
  for(let i=1;i<=8;i++){ await p.waitForTimeout(300); await snap('rel+'+i*300+'ms'); }
  const ev=await p.evaluate(()=>({
    marks:window.__tp.marks,
    bannerWrites:window.__tp.writes.filter(w=>/scrollback unavailable/.test(w.tag)).map(w=>Math.round(w.t)),
    bigWrites:window.__tp.writes.filter(w=>w.n>500).map(w=>`${Math.round(w.t)}:${w.n}`).slice(-8),
    resyncish:window.__tp.frames.filter(f=>f.k==='d'||f.k==='s').map(f=>`${Math.round(f.t)}:${f.k}:${f.n}`),
  }));
  console.log('EVENTS', JSON.stringify(ev,null,1));
  await c.close(); await b.close();
})().catch(e=>{console.error('PROBE ERROR',e&&(e.stack||e.message));process.exit(2);});
