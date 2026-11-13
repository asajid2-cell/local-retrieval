'use strict';
/* Walk the PHONE path with a real 390x844 viewport and report what each surface actually renders.
 * Read-only except taps on the nav and the size chip (the same things a user taps).
 */
const { chromium } = require('C:/Users/Ahmed/mux-track/wt-grid-proof/relay/node_modules/playwright');
const { assertSession } = require('./probe_guard.cjs');
// hl-auth binds the session to the UA it was minted with, so a probe MUST present that one. The phone
// FORM FACTOR is the viewport (390x844, touch, dpr 3) - which is what the layout and isMobile() key on.
const UA='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.7827.55 Safari/537.36';
const BASE=process.env.MUX_BASE||'https://harmonizerlabs.cc/multiplex';
const S=process.env.MUX_SESSION||'tab-26f4';
const COOKIE=(process.env.MUX_COOKIE||'').trim();
const pairs=COOKIE.split(';').map(p=>p.trim()).filter(Boolean).map(p=>{const i=p.indexOf('=');return{name:p.slice(0,i),value:p.slice(i+1)}});
(async()=>{
  const b=await chromium.launch({headless:true,args:['--no-proxy-server']});
  const c=await b.newContext({viewport:{width:390,height:844},deviceScaleFactor:3,isMobile:true,hasTouch:true,locale:'en-US',userAgent:UA});
  await c.addCookies(pairs.map(x=>({...x,domain:'harmonizerlabs.cc',path:'/',httpOnly:true,secure:true,sameSite:'Lax'})));
  const p=await c.newPage();
  const errs=[];
  p.on('pageerror',e=>errs.push('PAGEERROR '+String(e&&e.message||e)));
  p.on('console',m=>{ if(m.type()==='error') errs.push('CONSOLE '+m.text().slice(0,300)); });
  await p.goto(`${BASE}/?s=${encodeURIComponent(S)}`,{waitUntil:'domcontentloaded',timeout:60000});
  await p.waitForTimeout(4000);
  const boot=await p.evaluate(()=>({hasTerm:typeof term!=='undefined', hasCurrent:typeof current!=='undefined'&&!!current,
    mobileView:typeof mobileView!=='undefined'?mobileView:null,
    isMobile:typeof isMobile==='function'?isMobile():null,
    xtermLoaded:!!document.querySelector('.xterm'),
    bodyText:(document.body.innerText||'').slice(0,200)}));
  console.log('BOOT', JSON.stringify(boot,null,1));
  console.log('ERRS', JSON.stringify(errs.slice(0,6),null,1));
  if(!boot.hasTerm){ await p.screenshot({path:'phone-broken.png'}); await c.close(); await b.close(); process.exit(0); }
  await p.waitForFunction(()=>{try{return typeof term!=='undefined'&&term.cols>1&&current}catch(e){return false}},null,{timeout:30000});
  await assertSession(p,S);
  await p.waitForTimeout(3000);
  const st=await p.evaluate(()=>({
    view:typeof mobileView!=='undefined'?mobileView:null,
    chip:((document.querySelector('#sizebtn')||{}).textContent||'').replace(/\s+/g,' ').trim(),
    termCols:term.cols,termRows:term.rows,
    paneW:document.querySelector('#term').clientWidth,
    paneH:document.querySelector('#term').clientHeight,
    font:term.options.fontSize,
    hostRow:(()=>{try{const s=(window._sessions||[]).find(x=>x.name===current);return s?{cols:s.cols,rows:s.rows,owner:s.owner}:null}catch(e){return null}})(),
    scrollW:document.documentElement.scrollWidth, innerW:window.innerWidth,
    overflowX:document.documentElement.scrollWidth>window.innerWidth,
    steps:document.querySelectorAll('#mobileNav button').length,
    navVisible:!!document.querySelector('[data-mobile-view="threads"]'),
  }));
  console.log('PHONE', JSON.stringify(st,null,1));
  // terminal surface: is text clipped horizontally?
  const t2=await p.evaluate(()=>{
    const h=document.querySelector('#term');
    const s=h.querySelector('.xterm-screen');
    return {screenW:s?Math.round(s.getBoundingClientRect().width):null, hostW:h.clientWidth,
      clipped: s ? Math.round(s.getBoundingClientRect().width)>h.clientWidth : null};
  });
  console.log('TERM', JSON.stringify(t2));
  await p.screenshot({path:'phone-terminal.png'});
  // threads
  await p.locator('[data-mobile-view="threads"]').click();
  await p.waitForTimeout(1200);
  const th=await p.evaluate(()=>({
    msgs:document.querySelectorAll('#threads .th-msg').length,
    first:(document.querySelector('#threads .th-body')||{}).textContent?.slice(0,120),
    overflowX:document.documentElement.scrollWidth>window.innerWidth,
    composerH:(document.querySelector('#composer')||{}).clientHeight,
  }));
  console.log('THREADS', JSON.stringify(th,null,1));
  await p.screenshot({path:'phone-threads.png'});
  await c.close(); await b.close();
})().catch(e=>{console.error('ERR',e&&(e.stack||e.message));process.exit(2);});
