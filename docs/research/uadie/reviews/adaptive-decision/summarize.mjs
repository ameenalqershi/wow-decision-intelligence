import fs from 'node:fs';
const root=new URL('./',import.meta.url);
const q=(a,p)=>{const x=[...a].sort((a,b)=>a-b);const j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const med=a=>q(a,.5);
const out={};
for(const split of ['dev','holdout']){
 const rows=fs.readFileSync(new URL(split+'/raw.jsonl',root),'utf8').trim().split('\n').map(JSON.parse);
 const groups=new Map(),arms={};
 for(const r of rows){
  const k=[r.n,r.family,r.seed].join('/');
  if(!groups.has(k))groups.set(k,{});
  const a=groups.get(k);a[r.name]=(a[r.name]??0)+r.ticks*1e6/r.frequency;
  (arms[r.name]??=[]).push(r);
 }
 const compare=(name,base,subset)=>{
  const seeds=new Map();
  for(const [k,a] of subset){const seed=k.split('/')[2];if(!seeds.has(seed))seeds.set(seed,[]);seeds.get(seed).push(a[base]/a[name]);}
  const x=[...seeds.values()].map(med);
  let rng=71621;const rnd=()=>{rng=(Math.imul(rng,1664525)+1013904223)>>>0;return rng/2**32};
  const b=Array.from({length:2000},()=>med(Array.from({length:x.length},()=>x[Math.floor(rnd()*x.length)])));
  const ratios=subset.map(([,a])=>a[base]/a[name]);
  return {name,base,histories:ratios.length,medianSpeedup:med(ratios),wins:ratios.filter(x=>x>1).length,seedClusterMedian:med(x),ci95:[q(b,.025),q(b,.975)]};
 };
 const all=[...groups],summary=Object.entries(arms).map(([name,a])=>{
  const times=a.map(r=>r.ticks*1e6/r.frequency);
  return {name,rows:a.length,valid:a.filter(r=>r.valid).length,transported:a.filter(r=>r.Transported).length,eligibleRevisions:a.filter(r=>r.revision>0).length,medianUs:med(times),p90Us:q(times,.9),p95Us:q(times,.95),p99Us:q(times,.99),medianBytes:med(a.map(r=>r.bytes)),routes:Object.fromEntries([...new Set(a.map(r=>r.route))].map(k=>[k,a.filter(r=>r.route===k).length])),certificates:Object.fromEntries([...new Set(a.map(r=>r.certificate).filter(Boolean))].map(k=>[k,a.filter(r=>r.certificate===k).length])),attempts:a.reduce((s,r)=>s+(r.attempts??0),0),cacheHits:a.filter(r=>r.cacheHit).length};
 });
 const comparisons=['adaptive','hierarchy'].flatMap(name=>Object.keys(arms).filter(base=>base!==name).map(base=>compare(name,base,all)));
 const byFamily=[...new Set(rows.map(r=>r.family))].flatMap(f=>['warm','orientation','hierarchy'].map(base=>({family:f,...compare('adaptive',base,all.filter(([k])=>k.split('/')[1]===f))})));
 out[split]={rows:rows.length,valid:rows.filter(r=>r.valid).length,histories:groups.size,summary,comparisons,byFamily};
}
console.log(JSON.stringify(out,null,2));
