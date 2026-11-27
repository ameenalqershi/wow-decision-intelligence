// Structural visual review only; a passing check is not design approval.
import { readdirSync,readFileSync,writeFileSync,mkdtempSync,rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve,dirname,join } from 'node:path';
import { fileURLToPath,pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
const dir=dirname(fileURLToPath(import.meta.url));
const [chrome,output]=process.argv.slice(2);
if(!chrome||!output)throw Error('Usage: node check-layout.mjs CHROMIUM REPORT_JSON');
const files=readdirSync(dir).filter(n=>/^UI\d+\.svg$/.test(n)).sort();
assert.equal(files.length,21);
assert.ok(!files.includes('UI05.svg'),'Official reference must not be replaced');
const studies=files.map(n=>[n,readFileSync(join(dir,n),'utf8')]);
studies.unshift(['UI01.svg',readFileSync(resolve(dir,'../UI01/shell-study.svg'),'utf8')]);
const nav=['Overview','Decision Models','Decision Graph','Optimization','Scenarios','Simulation','Integrations','Benchmarks','Evidence','Settings'];
for(const [name,svg] of studies){
  for(const part of ['viewBox="0 0 720 460"','width="720" height="31"','width="144" height="407"','width="720" height="22"','width="375" height="276"','width="167" height="276"','design fixture']) assert.ok(svg.includes(part),name+': '+part);
  let previous=-1;
  for(const label of nav){const pos=svg.indexOf('>'+label+'</text>');assert.ok(pos>previous,name+': nav '+label);previous=pos;}
}
const tmp=mkdtempSync(join(tmpdir(),'wowdi-sp02-layout-'));
try {
  const html='<!doctype html><meta charset="utf-8"><body>'+studies.map(([n,s])=>'<section data-module="'+n+'">'+s+'</section>').join('')+`<pre id="report"></pre><script>
const result=[...document.querySelectorAll('section')].map(section=>{
 const svg=section.querySelector('svg'),boxes=[...svg.querySelectorAll('text')].map(n=>{const b=n.getBBox();return {text:n.textContent,x:b.x,y:b.y,w:b.width,h:b.height};});
 const outside=boxes.filter(b=>b.x<0||b.y<0||b.x+b.w>720.1||b.y+b.h>460.1);
 const collisions=[];for(let i=0;i<boxes.length;i++)for(let j=i+1;j<boxes.length;j++){const a=boxes[i],b=boxes[j];const w=Math.min(a.x+a.w,b.x+b.w)-Math.max(a.x,b.x),h=Math.min(a.y+a.h,b.y+b.h)-Math.max(a.y,b.y);if(w>1&&h>1)collisions.push([a.text,b.text]);}
 return {module:section.dataset.module,textCount:boxes.length,outside,collisions};
}); document.getElementById('report').textContent=JSON.stringify(result);
</script></body>`;
  const fixture=join(tmp,'fixture.html');
  writeFileSync(fixture,html);
  const args=['--headless','--disable-gpu','--disable-background-networking','--user-data-dir='+join(tmp,'profile'),'--dump-dom',pathToFileURL(fixture).href];
  if(process.getuid?.()===0)args.unshift('--no-sandbox');
  const run=spawnSync(chrome,args,{encoding:'utf8',timeout:60000,maxBuffer:16*1024*1024});
  assert.equal(run.status,0,run.stderr);
  const result=JSON.parse(run.stdout.match(/<pre id="report">(.*?)<\/pre>/s)[1].replaceAll('&amp;','&').replaceAll('&lt;','<').replaceAll('&gt;','>'));
  const version=spawnSync(chrome,['--version'],{encoding:'utf8'}).stdout.trim();
  writeFileSync(output,JSON.stringify({renderer:version,viewport:'720x460 logical SVG',studies:result,limitation:'Text bounding boxes and common reference geometry only; not visual approval, accessibility or functional tests.'},null,2)+'\n');
  assert.equal(result.length,22);
  for(const row of result){assert.ok(row.textCount>0,row.module);assert.equal(row.outside.length,0,row.module);assert.equal(row.collisions.length,0,row.module);}
  console.log('22 proposed studies: reference geometry, navigation order, text bounds and collision checks passed.');
} finally {rmSync(tmp,{recursive:true,force:true,maxRetries:5,retryDelay:200});}
