import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve,join } from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
const [browser,dir,report]=process.argv.slice(2);
if(!browser||!dir||!report)throw Error('Usage: browser-check.mjs CHROMIUM SVG_DIRECTORY REPORT');
const profile=await mkdtemp(join(tmpdir(),'wowdi-diagrams-'));
const args=['--headless','--disable-gpu','--no-first-run','--disable-background-networking','--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'];
if(process.getuid?.()===0)args.unshift('--no-sandbox');
const child=spawn(browser,args,{stdio:['ignore','ignore','pipe']});let stderr='',ws;
child.stderr.on('data',d=>stderr=(stderr+d).slice(-4000));
try{
 let port;
 for(let i=0;i<600;i++){try{port=(await readFile(join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0];break;}catch{if(child.exitCode!==null)throw Error(stderr);await new Promise(r=>setTimeout(r,100));}}
 assert.ok(port,stderr);
 const tabs=await(await fetch('http://127.0.0.1:'+port+'/json/list')).json();
 ws=new WebSocket(tabs.find(t=>t.type==='page').webSocketDebuggerUrl);
 await new Promise((res,rej)=>{ws.addEventListener('open',res,{once:true});ws.addEventListener('error',rej,{once:true});});
 let id=0;const pending=new Map();
 ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.id&&pending.has(m.id)){const p=pending.get(m.id);pending.delete(m.id);clearTimeout(p.timer);m.error?p.reject(Error(m.error.message)):p.resolve(m.result);}});
 const cdp=(method,params={})=>new Promise((resolve,reject)=>{const n=++id,timer=setTimeout(()=>{pending.delete(n);reject(Error(method));},15000);pending.set(n,{resolve,reject,timer});ws.send(JSON.stringify({id:n,method,params}));});
 const evaluate=async expression=>{const r=await cdp('Runtime.evaluate',{expression,returnByValue:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
 const results=[];
 for(const file of (await readdir(dir)).filter(x=>x.endsWith('.svg')).sort()){
  const url=pathToFileURL(resolve(dir,file)).href;
  await cdp('Page.navigate',{url});
  for(let i=0;i<100;i++){if(await evaluate('location.href==='+JSON.stringify(url)+'&&document.readyState==="complete"'))break;await new Promise(r=>setTimeout(r,50));}
  const r=await evaluate(`(()=>{
   const svg=document.documentElement,base=svg.viewBox.baseVal,inv=svg.getScreenCTM().inverse();
   const labels=[...svg.querySelectorAll('text')].map(e=>{const b=e.getBoundingClientRect();const p=new DOMPoint(b.left,b.top).matrixTransform(inv),q=new DOMPoint(b.right,b.bottom).matrixTransform(inv);return {text:e.textContent,x:p.x,y:p.y,w:q.x-p.x,h:q.y-p.y};});
   const outside=labels.filter(b=>b.x<base.x-2||b.y<base.y-2||b.x+b.w>base.x+base.width+2||b.y+b.h>base.y+base.height+2);
   const collisions=[];for(let i=0;i<labels.length;i++)for(let j=i+1;j<labels.length;j++){let a=labels[i],b=labels[j];if(Math.min(a.x+a.w,b.x+b.w)-Math.max(a.x,b.x)>2&&Math.min(a.y+a.h,b.y+b.h)-Math.max(a.y,b.y)>2)collisions.push([a.text,b.text]);}
   return {viewBox:[base.width,base.height],labels:labels.length,outside:outside.map(x=>x.text),collisions,links:svg.querySelectorAll('a').length};
  })()`);
  results.push({file,...r});
 }
 const result={browser:await cdp('Browser.getVersion'),diagrams:results,scope:'Actual SVG text geometry; not product UI or production deployment'};
 await writeFile(report,JSON.stringify(result,null,2)+'\n');
 console.log(JSON.stringify(results.map(x=>({file:x.file,labels:x.labels,outside:x.outside,collisions:x.collisions})),null,2));
 assert.ok(results.length>=15&&results.every(x=>x.outside.length===0&&x.collisions.length===0&&x.links>0),'SVG text geometry failed');
}finally{ws?.close();child.kill();await new Promise(r=>{if(child.exitCode!==null)r();else{child.once('exit',r);setTimeout(r,2000);}});await rm(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});}
