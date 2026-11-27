// Test tooling only. Uses a local Chromium and its DevTools protocol, no npm dependencies.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
const [browser, fixture, output] = process.argv.slice(2);
if (!browser || !fixture || !output) throw Error('Usage: node browser-check.mjs CHROMIUM GALLERY_URL SCREENSHOT_PNG');
const profile = await mkdtemp(join(tmpdir(), 'wowdi-ds03-'));
const args = ['--headless','--disable-gpu','--no-first-run','--disable-background-networking','--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'];
if (process.getuid?.() === 0) args.unshift('--no-sandbox'); // isolated local fixture on root-owned test host
const child = spawn(browser, args, { stdio: ['ignore','ignore','pipe'] });
let startupLog = '', startupError;
child.stderr.on('data', b => { startupLog = (startupLog + b.toString()).slice(-8192); });
child.on('error', e => { startupError = e; });
let ws;
try {
  let port;
  for (let i=0;i<600;i++) {
    if (startupError) throw startupError;
    if (child.exitCode !== null) throw Error("Chromium exited before debugging port: " + child.exitCode + "\n" + startupLog);
    try { port = (await readFile(join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0]; break; }
    catch { await new Promise(r=>setTimeout(r,100)); }
  }
  assert.ok(port, 'Chromium debugging port: ' + startupLog);
  const tabs = await (await fetch('http://127.0.0.1:'+port+'/json/list')).json();
  ws = new WebSocket(tabs.find(t=>t.type==='page').webSocketDebuggerUrl);
  await new Promise((res,rej)=>{ ws.addEventListener('open',res,{once:true}); ws.addEventListener('error',rej,{once:true}); });
  let id=0;
  const pending = new Map();
  ws.addEventListener('message', e=>{ const m=JSON.parse(e.data); if (m.id && pending.has(m.id)) {const p=pending.get(m.id); pending.delete(m.id); clearTimeout(p.timer); m.error?p.reject(Error(m.error.message)):p.resolve(m.result); } });
  const cdp = (method,params={})=>new Promise((resolve,reject)=>{const n=++id;const timer=setTimeout(()=>{pending.delete(n);reject(Error('Timeout '+method));},10000);pending.set(n,{resolve,reject,timer});ws.send(JSON.stringify({id:n,method,params}));});
  const evaluate=async expression=>{const r=await cdp('Runtime.evaluate',{expression,returnByValue:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
  const wait=async expr=>{for(let i=0;i<100;i++){if(await evaluate(expr))return;await new Promise(r=>setTimeout(r,100));}throw Error('Timeout: '+expr);};
  const key=async(name,code,number,modifiers=0)=>{
    await cdp('Input.dispatchKeyEvent',{type:name==='Enter'?'keyDown':'rawKeyDown',key:name,code,windowsVirtualKeyCode:number,modifiers,...(name==='Enter'?{text:'\r'}:{})});
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',key:name,code,windowsVirtualKeyCode:number,modifiers});
  };
  const tab=()=>key('Tab','Tab',9),enter=()=>key('Enter','Enter',13);
  await cdp('Emulation.setDeviceMetricsOverride',{width:1000,height:700,deviceScaleFactor:1,mobile:false});
  await cdp('Page.navigate',{url:fixture});
  await wait("document.querySelector('[data-interactive=true]') !== null");
  const state=()=>evaluate(`(()=>{const a=document.querySelector('.wow-language'),panes=[...document.querySelectorAll('.wow-logical-pane')],rects=panes.map(p=>p.getBoundingClientRect());return {lang:a.lang,dir:getComputedStyle(a).direction,dpr:devicePixelRatio,firstX:rects[0].x,secondX:rects[1].x,firstY:rects[0].y,secondY:rects[1].y,noOverflow:document.documentElement.scrollWidth<=innerWidth,ids:[...document.querySelectorAll('bdi')].map(b=>({text:b.textContent,direction:getComputedStyle(b).direction,bidi:getComputedStyle(b).unicodeBidi}))};})()`);
  const english=await state();
  assert.equal(english.lang,'en');assert.equal(english.dir,'ltr');assert.ok(english.firstX<english.secondX);
  await tab();await wait("document.activeElement.id === 'switch'");await enter();
  await wait("document.querySelector('.wow-language').lang === 'ar'");
  const arabic=await state();
  assert.equal(arabic.dir,'rtl');assert.ok(arabic.firstX>arabic.secondX);assert.ok(arabic.noOverflow);
  assert.ok(arabic.ids.every(i=>i.direction==='ltr'&&i.bidi==='isolate'));
  assert.equal(arabic.ids[0].text,'revision-17-A');assert.equal(arabic.ids[2].text,'run-7-B');assert.ok(arabic.ids[1].text.endsWith('ms'));
  await tab();await wait("document.activeElement.id === 'primary'");
  await tab();await wait("document.activeElement.id === 'secondary'");
  await cdp('Emulation.setDeviceMetricsOverride',{width:600,height:700,deviceScaleFactor:2,mobile:false});
  const compact=await state();
  assert.equal(compact.dpr,2);assert.ok(compact.firstY<compact.secondY);assert.ok(compact.noOverflow);
  assert.equal(await evaluate("document.activeElement.id"),'secondary');
  await key('Tab','Tab',9,8);await wait("document.activeElement.id === 'primary'");
  await key('Tab','Tab',9,8);await wait("document.activeElement.id === 'switch'");
  const png=await cdp('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});
  await writeFile(output,Buffer.from(png.data,'base64'));
  await enter();await wait("document.querySelector('.wow-language').lang === 'en'");
  assert.equal(await evaluate("document.activeElement.id"),'switch');
  console.log(JSON.stringify({version:await cdp('Browser.getVersion'),english,arabic,compact,checks:['Runtime language switch, direction and focus preserved','Keyboard focus order in RTL and stacked layout','Bidi isolated identifiers and unit labels','Responsive split at emulated DPR 1 and 2'],limitation:'Browser device-scale emulation, not physical monitor transfer'},null,2));
} finally {
  ws?.close(); child.kill();
  await new Promise(r=>{if(child.exitCode!==null)r();else {child.once('exit',r);setTimeout(r,2000);}});
  await rm(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
