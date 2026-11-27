// Test tooling only. Uses a local Chromium and its DevTools protocol, no npm dependencies.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
const [browser, fixture, output] = process.argv.slice(2);
if (!browser || !fixture || !output) throw Error('Usage: node browser-check.mjs CHROMIUM GALLERY_URL SCREENSHOT_PNG');
const profile = await mkdtemp(join(tmpdir(), 'wowdi-ds04-'));
const args = ['--headless','--disable-gpu','--no-first-run','--disable-background-networking','--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'];
if (process.getuid?.() === 0) args.unshift('--no-sandbox'); // isolated local fixture on root-owned test host
const child = spawn(browser, args, { stdio: 'ignore' });
let ws;
try {
  let port;
  for (let i=0;i<300;i++) {
    try { port = (await readFile(join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0]; break; }
    catch { await new Promise(r=>setTimeout(r,100)); }
  }
  assert.ok(port, 'Chromium debugging port');
  const tabs = await (await fetch('http://127.0.0.1:'+port+'/json/list')).json();
  ws = new WebSocket(tabs.find(t=>t.type==='page').webSocketDebuggerUrl);
  await new Promise((res,rej)=>{ ws.addEventListener('open',res,{once:true}); ws.addEventListener('error',rej,{once:true}); });
  let id=0;
  const pending = new Map();
  ws.addEventListener('message', e=>{ const m=JSON.parse(e.data); if (m.id && pending.has(m.id)) {const p=pending.get(m.id); pending.delete(m.id); clearTimeout(p.timer); m.error?p.reject(Error(m.error.message)):p.resolve(m.result); } });
  const cdp = (method,params={})=>new Promise((resolve,reject)=>{const n=++id;const timer=setTimeout(()=>{pending.delete(n);reject(Error('Timeout '+method));},10000);pending.set(n,{resolve,reject,timer});ws.send(JSON.stringify({id:n,method,params}));});
  const evaluate=async expression=>{const r=await cdp('Runtime.evaluate',{expression,returnByValue:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
  await cdp('Emulation.setDeviceMetricsOverride',{width:1000,height:900,deviceScaleFactor:1,mobile:false});
  await cdp('Page.navigate',{url:fixture});
  const wait=async expr=>{for(let i=0;i<100;i++){if(await evaluate(expr))return;await new Promise(r=>setTimeout(r,100));}throw Error('Timeout: '+expr+'; focus='+await evaluate("document.activeElement?.outerHTML?.slice(0,300)"));};
  const key=async(name,code,number)=>{
    await cdp('Input.dispatchKeyEvent',{type:name==='Enter'?'keyDown':'rawKeyDown',key:name,code,windowsVirtualKeyCode:number,...(name==='Enter'?{text:'\r'}:{})});
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',key:name,code,windowsVirtualKeyCode:number});
  };
  const tab=()=>key('Tab','Tab',9),enter=()=>key('Enter','Enter',13);
  const checks=[];
  await wait("document.querySelector('[data-interactive=true]') !== null");
  await tab();
  await wait("document.activeElement.dataset.edgeId === 'input-scope'");
  await enter();
  await wait("document.querySelector('.wow-graph-svg button').getAttribute('aria-pressed') === 'true'");
  const incidence=await evaluate(`(()=>{const links=[...document.querySelectorAll('line[data-edge-id=input-scope]')];const row=document.querySelector('.wow-visual-table tbody tr');return {graph:links.map(l=>[l.dataset.role,l.dataset.entityId]),table:[['owner',row.querySelector('[data-owner]').dataset.owner],...[...row.querySelectorAll('[data-target]')].map(x=>['target',x.dataset.target])]};})()`);
  assert.deepEqual(incidence.graph,incidence.table);
  assert.deepEqual(incidence.graph,[['owner','optimizer'],['target','demand'],['target','materials']]);
  const ax=await cdp('Accessibility.getFullAXTree');
  assert.ok(ax.nodes.some(n=>!n.ignored&&n.role?.value==='button'&&n.name?.value?.includes('input-scope')));
  assert.ok(ax.nodes.some(n=>!n.ignored&&n.role?.value==='table'&&n.name?.value==='Relationship incidence'));
  assert.equal(await evaluate("document.querySelector('.wow-visual-table button').getAttribute('aria-pressed')"),'true');
  checks.push('Keyboard selects SVG hyperedge; typed incidence equals accessible table and selection is synchronized');
  await tab(); await tab();
  await wait("document.activeElement.getAttribute('aria-label')?.startsWith('Event event-1:')");
  await enter();
  await wait("document.querySelector('[aria-label^=\x22Event event-1:\x22]').getAttribute('aria-pressed') === 'true'");
  assert.ok(await evaluate("document.querySelectorAll('.wow-visual-table')[1].querySelector('button').getAttribute('aria-pressed') === 'true'"));
  const chart=await evaluate(`({segments:[...document.querySelectorAll('polyline')].map(e=>e.dataset.segment),observed:[...document.querySelectorAll('circle[data-event-id]')].map(e=>e.dataset.eventId),missing:[...document.querySelectorAll('td[data-missing=true]')].map(e=>e.parentElement.querySelector('button').textContent)})`);
  assert.deepEqual(chart.segments,['event-1,event-2','event-4,event-5']);
  assert.deepEqual(chart.observed,['event-1','event-2','event-4','event-5']);
  assert.deepEqual(chart.missing,['event-3']);
  checks.push('Keyboard-selected observed event matches stored fixture event ID; null observation has no point and splits line');
  assert.equal(await evaluate("document.querySelectorAll('.wow-visual-table')[1].querySelector('tr:last-child td:last-child').textContent"),'0');
  checks.push('Zero remains observed, missing value is explicitly unavailable');
  const dims=await evaluate("({width:window.innerWidth,height:document.documentElement.scrollHeight})");
  await evaluate("window.scrollTo(0,0)");
  const png=await cdp('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:dims.width,height:dims.height,scale:1}});
  await writeFile(output,Buffer.from(png.data,'base64'));
  const version=await cdp('Browser.getVersion');
  console.log(JSON.stringify({version,checks,incidence,chart,dims},null,2));
} finally {
  ws?.close(); child.kill();
  await new Promise(r=>{if(child.exitCode!==null)r();else {child.once('exit',r);setTimeout(r,2000);}});
  await rm(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
