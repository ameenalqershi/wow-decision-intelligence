import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {spawnSync} from 'node:child_process';
import assert from 'node:assert/strict';
const dir=path.dirname(fileURLToPath(import.meta.url));
const [chrome,output,sheets]=process.argv.slice(2);
if(!chrome||!output)throw Error('Usage: node check-variants.mjs CHROMIUM REPORT [SCREENSHOT_DIR]');
const files=['states','wireframes','rtl'].flatMap(group=>fs.readdirSync(path.join(dir,group)).filter(n=>n.endsWith('.svg')).map(n=>[group+'/'+n,fs.readFileSync(path.join(dir,group,n),'utf8')]));
assert.equal(files.length,184);
const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'wowdi-variants-'));
const run=(args)=>{const proc=spawnSync(chrome,[...(process.getuid?.()===0?['--no-sandbox']:[]),'--headless','--disable-gpu','--disable-background-networking','--user-data-dir='+path.join(tmp,'profile'),...args],{encoding:'utf8',timeout:90000,maxBuffer:32*1024*1024});assert.equal(proc.status,0,proc.stderr);return proc.stdout;};
try{
 let results=[];
 for(let i=0;i<files.length;i+=24){
  const html='<!doctype html><meta charset="utf-8"><body>'+files.slice(i,i+24).map(([n,s])=>'<section data-name="'+n+'">'+s+'</section>').join('')+`<pre id="report"></pre><script>
const rows=[...document.querySelectorAll('section')].map(section=>{
 const svg=section.querySelector('svg'),boxes=[...svg.querySelectorAll('text')].map(n=>{const b=n.getBBox();return {text:n.textContent,x:b.x,y:b.y,w:b.width,h:b.height};});
 const outside=boxes.filter(b=>b.x<-.1||b.y<-.1||b.x+b.w>720.1||b.y+b.h>460.1);
 const collisions=[];for(let i=0;i<boxes.length;i++)for(let j=i+1;j<boxes.length;j++){const a=boxes[i],b=boxes[j];const w=Math.min(a.x+a.w,b.x+b.w)-Math.max(a.x,b.x),h=Math.min(a.y+a.h,b.y+b.h)-Math.max(a.y,b.y);if(w>1&&h>1)collisions.push([a.text,b.text]);}
 const disabled=section.dataset.name.startsWith('states/')&&!section.dataset.name.endsWith('-actions.svg')?svg.querySelectorAll('[data-control="Commit or execute"][data-enabled="false"]').length===1:null;
 return {file:section.dataset.name,textCount:boxes.length,outside,collisions,mutationDisabled:disabled};
});document.getElementById('report').textContent=JSON.stringify(rows);
</script></body>`;
  const file=path.join(tmp,'batch.html');fs.writeFileSync(file,html);
  const stdout=run(['--dump-dom',pathToFileURL(file).href]);
  const batch=JSON.parse(stdout.match(/<pre id="report">(.*?)<\/pre>/s)[1].replaceAll('&amp;','&').replaceAll('&lt;','<').replaceAll('&gt;','>'));
  results.push(...batch);
 }
 fs.writeFileSync(output,JSON.stringify({renderer:spawnSync(chrome,['--version'],{encoding:'utf8'}).stdout.trim(),viewport:'720×460 logical SVG',results,limitation:'Static design rendering only. Native UI and runtime acceptance are not claimed.'},null,2)+'\n');
 const failures=results.filter(x=>x.outside.length||x.collisions.length||x.mutationDisabled===false||x.textCount===0);
 if(failures.length)console.log(JSON.stringify(failures,null,2));
 assert.equal(failures.length,0);
 assert.equal(results.length,184);
 if(sheets){
  fs.mkdirSync(sheets,{recursive:true});
  const groups=[['states/UI04-invalid.svg','states/UI11-loading.svg','states/UI16-unavailable.svg','states/UI17-failed.svg'],['states/UI14-actions.svg','states/UI20-actions.svg','rtl/UI04.svg','rtl/UI23.svg']];
  for(let i=0;i<groups.length;i++){
   const html='<!doctype html><meta charset=utf-8><style>html,body{margin:0;overflow:hidden}body{display:grid;grid-template-columns:720px 720px;gap:8px;background:#e2e8f0;padding:8px}section{width:720px}h2{font:14px Arial;margin:0;padding:4px}svg{display:block}</style>'+groups[i].map(n=>'<section><h2>'+n+'</h2>'+fs.readFileSync(path.join(dir,n),'utf8')+'</section>').join('');
   const file=path.join(tmp,'sheet.html');fs.writeFileSync(file,html);
   run(['--window-size=1464,1100','--force-device-scale-factor=1','--screenshot='+path.resolve(sheets,'variants-'+i+'.png'),pathToFileURL(file).href]);
  }
 }
 console.log('184 visual variants passed text geometry and static mutation-disabled checks.');
}finally{fs.rmSync(tmp,{recursive:true,force:true,maxRetries:5,retryDelay:200});}
