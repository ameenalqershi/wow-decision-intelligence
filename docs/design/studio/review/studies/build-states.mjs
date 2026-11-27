// Design-only variants. The archived UI05 SVG is never changed.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const dir=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(dir,'../../../../..');
const read=p=>fs.readFileSync(path.join(root,p),'utf8');
const data=JSON.parse(fs.readFileSync(path.join(dir,'state-fixtures.json'),'utf8'));
const acceptance=JSON.parse(read('docs/design/studio/review/modules/acceptance-data.json'));
const req=JSON.parse(read('planning/requirements.json'));
const out=path.join(dir,'states');fs.mkdirSync(out,{recursive:true});
const wireOut=path.join(dir,'wireframes');fs.mkdirSync(wireOut,{recursive:true});
const c={bg:'#10151e',panel:'#121b27',card:'#1c2735',edge:'#2c3949',chrome:'#1b2331',text:'#e2e8f0',muted:'#94a3b8',focus:'#62e4c6',warn:'#facc70',error:'#ffb4ab',button:'#16785f'};
const esc=s=>String(s).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const r=(x,y,w,h,fill,stroke='none',rx=4)=>'<rect x="'+x+'" y="'+y+'" width="'+w+'" height="'+h+'" rx="'+rx+'" fill="'+fill+'" stroke="'+stroke+'"/>';
const t=(x,y,s,size=11,fill=c.text,weight=400)=>'<text x="'+x+'" y="'+y+'" font-size="'+size+'" fill="'+fill+'" font-weight="'+weight+'">'+esc(s)+'</text>';
const states=['loading','empty','invalid','unavailable','failed'];
const labels=['Loading','Empty','Invalid input','Unavailable','Failed'];
const nav=['Overview','Decision Models','Decision Graph','Optimization','Scenarios','Simulation','Integrations','Benchmarks','Evidence','Settings'];
const ref=read('docs/design/references/01-WOW-DI-Studio.svg');
const source=id=>id==='UI01'?read('docs/design/studio/review/UI01/shell-study.svg'):id==='UI05'?ref:fs.readFileSync(path.join(dir,id+'.svg'),'utf8');
const palette={'#10151e':'#f5f7fa','#151d29':'#e8edf2','#1b2331':'#e2e8f0','#1c2735':'#ffffff','#2c3949':'#a0aab7','#121b27':'#ffffff','#1b2533':'#ffffff','#e2e8f0':'#17212e','#94a3b8':'#435367','#aebacb':'#304155','#8292a7':'#526477','#23463f':'#d2eee5','#62e4c6':'#105344','#3ed5b3':'#116b55','#36c9aa':'#116b55','#5be1c2':'#116b55','#20374d':'#dce9f4','#4276a0':'#3d6a92','#477d9e':'#37688d','#20594d':'#d2eee5','#4cddb4':'#176853','#facc70':'#794b05','#ffb4ab':'#a22020'};
const manifest=[];
for(const [id,messages] of Object.entries(data)){
 const svg=source(id);
 // Wireframe retains the reviewed, module-specific information architecture.
 const wire=svg.replace(/#[0-9a-f]{6}\b/gi,m=>palette[m.toLowerCase()]??m).replace('viewBox="0 0 720 460"','viewBox="0 0 720 460"');
 fs.writeFileSync(path.join(wireOut,id+'.svg'),wire);
 const title=req.find(x=>x.issue===id)?.requirements?.[0]?.title;assert.ok(title,id);
 // Preserve actual per-module title and chrome but replace data area with scoped state.
 const prefix=svg.slice(0,svg.indexOf('<text x="158"'));
 assert.ok(prefix.includes('width="144"'),id);
 const headerTitle=svg.match(/<text x="158" y="54"[^>]*>([^<]+)<\/text>/)?.[1]??id;
 for(let i=0;i<states.length;i++){
   const kind=states[i],label=labels[i],msg=messages[i];
   let v=prefix.replace(/<title>.*?<\/title>/,'');
   v+=t(158,54,headerTitle,15,c.text,700)+t(158,70,id+' / design fixture / '+label,9,c.muted);
   v+=r(157,82,552,58,c.card,c.edge,5)+t(169,101,'Selected context',9,c.muted)+t(169,119,'No unconfirmed result is published',11);
   v+=r(157,152,375,276,c.panel,c.edge,5)+t(169,172,label,11,c.text,700);
   const color=i===2||i===4?c.error:i===3?c.warn:c.muted;
   v+=r(169,188,351,101,c.card,c.edge,5)+t(181,210,msg,10,color,700);
   const detail=['Wait for an authoritative response.','Choose an explicit context.','Keep the previous valid state.','No result or value is inferred.','Retry explicitly; no automatic mutation.'][i];
   v+=t(181,235,detail,10,c.text);
   if(i===0){v+=r(181,253,325,8,'#344255')+r(181,253,70,8,'#35c6a2')+t(181,275,'Indeterminate — not a completion percentage',9,c.muted);}
   else{const action=messages[i===1?5:6];v+='<g data-enabled="true" data-control="'+esc(action)+'">'+r(181,250,192,24,c.button,c.focus)+t(189,266,action,10,'#ffffff')+'</g>';}
   v+=t(169,316,'Keyboard recovery',10,c.text,700)+t(169,336,'Tab: next available control',10,c.muted)+t(169,354,'Esc: return to previous safe context',10,c.muted)+t(169,378,'Focus remains visible on correction or retry.',10,c.muted);
   v+=r(542,152,167,276,'#1b2533',c.edge,5)+t(553,172,'Context and diagnostics',11,c.text,700);
   v+=t(553,195,'Revision / scope',9,c.muted)+t(553,211,'Not confirmed',10)+t(553,240,'Provider outcome',9,c.muted)+t(553,256,label,10,color)+t(553,285,'Previous valid state',9,c.muted)+t(553,301,'Preserved if available',10);
   v+='<g data-enabled="false" data-control="Commit or execute">'+r(553,397,145,24,c.card,c.edge)+t(561,413,'Commit or execute',10,c.muted)+'</g>';
   v+=r(0,438,720,22,c.chrome)+t(12,452,'Engine status: not confirmed',9,c.muted)+t(558,452,'Design fixture / '+id,9,c.muted)+'</svg>';
   fs.writeFileSync(path.join(out,id+'-'+kind+'.svg'),v+'\n');
   manifest.push({module:id,state:kind,fixture:'states/'+id+'-'+kind+'.svg',message:msg,mutationEnabled:false,recovery:i===0?null:messages[i===1?5:6]});
 }
 // A second, detailed action surface enumerates the exact feature controls.
 const actions=acceptance[id].cases;
 let v=prefix.replace(/<title>.*?<\/title>/,'')+t(158,54,headerTitle,15,c.text,700)+t(158,70,id+' / design fixture / action surface',9,c.muted);
 v+=r(157,82,552,58,c.card,c.edge,5)+t(169,101,'Available actions',9,c.muted)+t(169,119,'Select context and validate permissions before execution',10);
 v+=r(157,152,552,276,c.panel,c.edge,5)+t(169,172,'Registered module controls',11,c.text,700);
 const reqs=req.find(x=>x.issue===id).requirements;
 for(let i=0;i<actions.length;i++){
   const y=182+i*29;assert.ok(y+25<=428,id);
   v+=r(169,y,528,25,c.card,c.edge,3)+t(178,y+16,reqs[i].id,9,c.muted)+t(245,y+16,actions[i][0],10,c.text)+t(624,y+16,'Scoped',9,c.muted);
 }
 v+=r(0,438,720,22,c.chrome)+t(12,452,'Action surface — provider admission required',9,c.muted)+t(558,452,'Design fixture / '+id,9,c.muted)+'</svg>';
 fs.writeFileSync(path.join(out,id+'-actions.svg'),v+'\n');
}
assert.equal(manifest.length,115);
fs.writeFileSync(path.join(dir,'state-manifest.json'),JSON.stringify(manifest,null,2)+'\n');
console.log('Generated 115 module-specific state variants, 23 action surfaces and 23 structural wireframes.');
