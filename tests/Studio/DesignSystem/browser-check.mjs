// Test tooling only. Uses a local Chromium and its DevTools protocol, no npm dependencies.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
const [browser, fixture, output] = process.argv.slice(2);
if (!browser || !fixture || !output) throw Error('Usage: node browser-check.mjs CHROMIUM GALLERY_HTML SCREENSHOT_PNG');
const profile = await mkdtemp(join(tmpdir(), 'wowdi-ds01-'));
const args = ['--headless','--disable-gpu','--no-first-run','--disable-background-networking','--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'];
if (process.getuid?.() === 0) args.unshift('--no-sandbox'); // isolated local fixture on root-owned test host
const child = spawn(browser, args, { stdio: ['ignore','ignore','pipe'] });
let ws;
let startupLog = '';
child.stderr.on('data', b => { startupLog = (startupLog + b.toString()).slice(-8192); });
try {
  let port;
  for (let i=0;i<300;i++) {
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
  await cdp('Emulation.setDeviceMetricsOverride',{width:720,height:460,deviceScaleFactor:1,mobile:false});
  await cdp('Page.navigate',{url:pathToFileURL(resolve(fixture)).href});
  for (let i=0;i<300;i++) {if(await evaluate("document.readyState === 'complete' && document.querySelectorAll('.wow-theme').length === 3"))break;await new Promise(r=>setTimeout(r,100));}
  const computed=await evaluate(`Array.from(document.querySelectorAll('.wow-theme'),t=>{
    const s=(selector)=>getComputedStyle(t.querySelector(selector));
    return { theme:t.dataset.wowTheme, background:getComputedStyle(t).backgroundColor, text:getComputedStyle(t).color,
      card:s('.sample-card').backgroundColor, button:s('.action').backgroundColor, font:s('.metric').fontSize,
      icon:s('.wow-icon').width, target:s('.control').minBlockSize, stateCount:t.querySelectorAll('.success,.warning,.error').length };
  })`);
  assert.equal(computed.length,3);
  assert.deepEqual(computed.map(t=>t.background),['rgb(16, 21, 30)','rgb(245, 247, 250)','rgb(0, 0, 0)']);
  assert.deepEqual(computed.map(t=>t.card),['rgb(28, 39, 53)','rgb(255, 255, 255)','rgb(0, 0, 0)']);
  assert.deepEqual(computed.map(t=>t.text),['rgb(226, 232, 240)','rgb(23, 33, 46)','rgb(255, 255, 255)']);
  assert.ok(computed.every(t=>t.font==='24px'&&t.icon==='16px'&&t.target==='24px'&&t.stateCount===3));
  const focus=[];
  for (let i=0;i<6;i++) {
    await cdp('Input.dispatchKeyEvent',{type:'keyDown',key:'Tab',code:'Tab',windowsVirtualKeyCode:9});
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',key:'Tab',code:'Tab',windowsVirtualKeyCode:9});
    focus.push(await evaluate(`(()=>{const a=document.activeElement,s=getComputedStyle(a);return {theme:a.closest('.wow-theme')?.dataset.wowTheme,role:a.className,visible:a.matches(':focus-visible'),width:s.outlineWidth,color:s.outlineColor,style:s.outlineStyle};})()`));
  }
  assert.deepEqual(focus.map(f=>f.theme),['dark','dark','light','light','high-contrast','high-contrast']);
  assert.deepEqual(focus.map(f=>f.role),['action','control','action','control','action','control']);
  assert.ok(focus.every(f=>f.visible&&f.width==='2px'&&f.style==='solid'));
  assert.deepEqual(focus.map(f=>f.color),['rgb(98, 228, 198)','rgb(98, 228, 198)','rgb(7, 89, 160)','rgb(7, 89, 160)','rgb(255, 255, 0)','rgb(255, 255, 0)']);
  await evaluate("document.activeElement.blur(); window.scrollTo(0,0)");
  const dims=await evaluate("({width:window.innerWidth,height:document.documentElement.scrollHeight})");
  const png=await cdp('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:dims.width,height:dims.height,scale:1}});
  await writeFile(output,Buffer.from(png.data,'base64'));
  const version=await cdp("Browser.getVersion");
  console.log(JSON.stringify({version,computed,focus,dims},null,2));
} finally {
  ws?.close();
  child.kill();
  await new Promise(r=>{if(child.exitCode!==null)r();else {child.once('exit',r);setTimeout(r,2000);}});
  await rm(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
