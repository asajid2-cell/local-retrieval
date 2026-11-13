'use strict';
/* How long does RELEASE take, measured from the tap to the moment the pane is local again?
 *
 * The report is "trying to give it back glitches and takes a while". This takes the grid first (so there is
 * something to release), then taps once and samples until the chip says local, recording every frame so a
 * stall can be attributed. Read-only apart from the taps the user themselves performs.
 * Usage: MUX_SESSION=<name> node release_probe.cjs
 */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
const UA='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE=process.env.MUX_BASE||'https://harmonizerlabs.cc/multiplex';
const S=process.env.MUX_SESSION||'tab-26f4';
const COOKIE=(process.env.MUX_COOKIE||'').trim();
const pairs=COOKIE.split(';').map(p=>p.trim()).filter(Boolean).map(p=>{const i=p.indexOf('=');return{name:p.slice(0,i),value:p.slice(i+1)}});
(async()=>{
  const b=await chromium.launch({headless:true,args:['--no-proxy-server']});
  const c=await b.newContext({viewport:{width:1440,height:900},locale:'en-US',userAgent:UA});
  await c.addCookies(pairs.map(x=>({...x,domain:'harmonizerlabs.cc',path:'/',httpOnly:true,secure:true,sameSite:'Lax'})));
  const p=await c.newPage();
  await p.addInitScript(()=>{
    window.__rp={writes:[]};
    const dec=new TextDecoder('utf-8');
    const boot=()=>{ if(typeof term==='undefined'||!term||!term.write){setTimeout(boot,5);return;}
      const ow=term.write.bind(term);
      term.write=(d,cb)=>{ const s=typeof d==='string'?d:dec.decode(d);
        if(/scrollback unavailable/.test(s)) window.__rp.writes.push({t:performance.now(),banner:1});
        return ow(d,cb); };
    };
    boot();
  });
  await p.goto(`${BASE}/?s=${encodeURIComponent(S)}`,{waitUntil:'domcontentloaded',timeout:60000});
  await p.waitForFunction(()=>{try{return typeof term!=='undefined'&&term.cols>1&&current}catch(e){return false}},null,{timeout:30000});
  await assertSession(p,S);
  await p.waitForTimeout(2500);
  const st=()=>p.evaluate(()=>({chip:((document.querySelector('#sizebtn')||{}).textContent||'').replace(/\s+/g,' ').trim(),
    owner:(()=>{try{return sizeGridOwner}catch(e){return null}})(),mine:(()=>{try{return sizeMine}catch(e){return null}})(),
    busy:(()=>{try{return _gridBusy}catch(e){return null}})(),cols:term.cols,rows:term.rows}));
  const tap=()=>p.evaluate(()=>document.querySelector('#sizebtn').click());
  const waitStable=async(label,ms=9000)=>{
    const t0=Date.now();
    while(Date.now()-t0<ms){ const s=await st(); if(s.pinned===undefined&&s.busy===null&&s.mine!==undefined){} 
      if(!s.busy) return {ms:Date.now()-t0,s}; await p.waitForTimeout(50);} return {ms:-1,s:await st()}; };
  // climb to a take: fill -> fit -> take
  let s=await st();
  for(let i=0;i<3 && !(s.mine);i++){ await tap(); await p.waitForTimeout(700); s=await st(); }
  console.log('taken', JSON.stringify(s));
  if(!s.mine){ console.log('could not take'); await b.close(); return; }
  // RELEASE, and time it
  const t0=Date.now();
  await tap();
  let settled=null;
  while(Date.now()-t0<9000){ const q=await st();
    if(!q.busy && q.mine===false){ settled={ms:Date.now()-t0,q}; break; }
    await p.waitForTimeout(40); }
  const banner=await p.evaluate(()=>window.__rp.writes.length);
  console.log('release', JSON.stringify({settled, bannerCount:banner}));
  await c.close(); await b.close();
})().catch(e=>{console.error('ERR',e&&(e.stack||e.message));process.exit(2);});
