// Test tooling only. Uses a local Chromium and its DevTools protocol, no npm dependencies.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
const [browser, fixture, output] = process.argv.slice(2);
if (!browser || !fixture || !output) throw Error('Usage: node browser-check.mjs CHROMIUM GALLERY_URL SCREENSHOT_PNG');
const profile = await mkdtemp(join(tmpdir(), 'wowdi-ds02-'));
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
  await cdp('Emulation.setDeviceMetricsOverride',{width:1000,height:900,deviceScaleFactor:1,mobile:false});
  await cdp('Page.navigate',{url:fixture});
  const wait = async (expr, label=expr) => {
    for(let i=0;i<100;i++){ if(await evaluate(expr))return; await new Promise(r=>setTimeout(r,100)); }
    throw Error('Timeout: '+label+'; active='+await evaluate("document.activeElement?.outerHTML"));
  };
  const key=async (name,code,number,modifiers=0)=>{
    await cdp('Input.dispatchKeyEvent',{type: name === 'Enter' ? 'keyDown' : 'rawKeyDown',key:name,code,windowsVirtualKeyCode:number,modifiers,...(name === 'Enter' ? {text:'\r'} : {})});
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',key:name,code,windowsVirtualKeyCode:number,modifiers});
  };
  const tab=()=>key('Tab','Tab',9), enter=()=>key('Enter','Enter',13), escape=()=>key('Escape','Escape',27);
  const active=()=>evaluate("document.activeElement?.id");
  const checks=[];
  await wait("document.querySelector('[data-interactive=true]') !== null");
  // Traverse from document start using keyboard, never programmatically focus a test target.
  await evaluate("document.activeElement.blur()");
  for(let i=0;i<12 && await active()!=='open-dialog';i++)await tab();
  assert.equal(await active(),'open-dialog');
  await enter();
  await wait("document.querySelector('dialog').open && document.activeElement.id === 'dialog-value'");
  let ax=await cdp('Accessibility.getFullAXTree');
  const named=(tree,role,name)=>tree.nodes.some(n=>!n.ignored&&n.role?.value===role&&n.name?.value===name);
  assert.ok(named(ax,'dialog','Edit fixture'));
  assert.ok(named(ax,'textbox','Dialog value'));
  await cdp('Input.insertText',{text:'Local edit'});
  await tab();
  await wait("document.activeElement.id === 'apply-dialog'");
  await tab();
  assert.equal(await evaluate("document.activeElement.textContent.trim()"),'Close');
  await tab();
  // Native dialog may move through browser chrome; no page control outside may receive focus.
  assert.ok(await evaluate("document.activeElement === document.body || document.querySelector('dialog').contains(document.activeElement)"));
  await escape();
  await wait("!document.querySelector('dialog').open && document.activeElement.id === 'open-dialog'");
  checks.push('Modal accessible names, keyboard traversal, Escape and focus restoration');
  await enter();
  await wait("document.querySelector('dialog').open && document.activeElement.id === 'dialog-value'");
  await tab(); await enter();
  await wait("!document.querySelector('dialog').open && document.activeElement.id === 'open-dialog' && document.querySelector('.wow-announcements [role=status]').textContent.includes('Dialog value applied.')");
  checks.push('Modal apply and polite live announcement');
  await tab();
  // Fluent custom element uses shadow DOM; document.activeElement is the host.
  await wait("document.activeElement.localName === 'fluent-button'");
  await enter();
  await wait("document.querySelector('.wow-announcements [role=status]').textContent.includes('Fluent action invoked.')");
  checks.push('Actual Fluent UI button keyboard activation');
  await tab();
  await wait("document.activeElement.getAttribute('aria-label') === 'Alpha Value'");
  await key('a','KeyA',65,2);
  await cdp('Input.insertText',{text:'Changed'});
  await tab();
  await wait("document.activeElement.getAttribute('aria-label') === 'Beta Value' && document.querySelector('.wow-announcements [role=status]').textContent.includes('Value updated: a')");
  assert.equal(await evaluate(`document.querySelector('[aria-label="Alpha Value"]').value`),'Changed');
  ax=await cdp('Accessibility.getFullAXTree');
  assert.ok(named(ax,'textbox','Alpha Value'));
  assert.ok(named(ax,'textbox','Beta Value'));
  checks.push('Editable table keyboard navigation, stable row identity and named fields');
  await tab(); await wait("document.activeElement.id === 'loading'"); await enter();
  await wait("document.querySelector('[data-state=loading][aria-busy=true]') !== null");
  await tab(); await wait("document.activeElement.id === 'empty'"); await enter();
  await wait("document.querySelector('[data-state=empty][role=status]') !== null");
  assert.equal(await evaluate("document.querySelector('[data-state=empty] [role=alert]')===null"),true);
  await tab(); await wait("document.activeElement.id === 'error'"); await enter();
  await wait("document.querySelector('[data-state=error] [role=alert]') !== null");
  ax=await cdp('Accessibility.getFullAXTree');
  assert.ok(ax.nodes.some(n=>!n.ignored&&n.role?.value==='alert'));
  assert.ok(await evaluate("document.querySelector('.wow-announcements [role=status][aria-live=polite]') && document.querySelector('.wow-announcements [role=alert]')"));
  assert.equal(await evaluate("document.querySelector('.wow-status').dataset.state"),'error');
  assert.equal(await evaluate("document.querySelector('.wow-status').textContent.trim()"),'Error');
  checks.push('Distinct loading, empty, error trees and live region semantics');
  // Shift+Tab to theme selector via keyboard.
  for(let i=0;i<20 && await active()!=='theme';i++)await key('Tab','Tab',9,8);
  assert.equal(await active(),'theme');
  await key('ArrowDown','ArrowDown',40); await key('Tab','Tab',9);
  await wait("document.querySelector('.wow-theme').dataset.wowTheme === 'light'");
  checks.push('Theme changes through the real Blazor event pipeline');
  const version=await cdp('Browser.getVersion');
  const dims=await evaluate("({width:window.innerWidth,height:document.documentElement.scrollHeight})");
  await evaluate("window.scrollTo(0,0)");
  const png=await cdp('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:dims.width,height:dims.height,scale:1}});
  await writeFile(output,Buffer.from(png.data,'base64'));
  console.log(JSON.stringify({version,checks,dims},null,2));
} finally {
  ws?.close();
  child.kill();
  await new Promise(r=>{if(child.exitCode!==null)r();else {child.once('exit',r);setTimeout(r,2000);}});
  await rm(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
