import fs from 'node:fs';
import crypto from 'node:crypto';
const dir=new URL('./',import.meta.url);
const read=f=>fs.readFileSync(new URL(f,dir),'utf8').trim().split('\n').map(JSON.parse);
const rows=read('raw.jsonl');
const q=(a,p)=>{const x=[...a].sort((a,b)=>a-b),j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const median=a=>q(a,.5);
const names=['warm','propagation','structural'];
const key=r=>[r.n,r.family,r.seed,r.revision].join('/');
const models=new Map(),histories=new Map();
for(const r of rows){
 const k=key(r);
 if(!models.has(k))models.set(k,r);
 if(r.digest!==models.get(k).digest||r.native!==models.get(k).native||r.status!==models.get(k).status)throw Error('inconsistent reference');
 const digest=crypto.createHash('sha256').update(JSON.stringify(r.model)).digest('hex').toUpperCase();
 if(digest!==r.digest)throw Error('model digest mismatch');
 const h=k.split('/').slice(0,3).join('/');
 if(!histories.has(h))histories.set(h,{});
 const a=histories.get(h);a[r.name]=(a[r.name]??0)+r.ticks*1e6/r.frequency;
}
const summary=names.map(name=>{
 const a=rows.filter(r=>r.name===name),persist=read('persisted/'+name+'.jsonl');
 if(persist.length!==a.length)throw Error('missing persisted');
 for(let i=0;i<a.length;i++){
  const r=a[i],v=persist[i];
  if(key(r)!==key(v)||r.digest!==v.digest||(v.Best?.Value??null)!==r.value)throw Error('persisted mismatch');
  if(v.Best){
   const mask=v.Best.Mask,m=r.model;
   const count=x=>x.toString(2).replace(/0/g,'').length;
   if(mask<0||mask>=2**m.Size||m.Capacities.some(c=>count(mask&c.Scope)<c.Lower||count(mask&c.Scope)>c.Upper)||m.Costs.filter(c=>(mask&c.Scope)===c.Scope).reduce((s,c)=>s+c.Weight,0)!==r.value)throw Error('readback witness');
  }
 }
 const us=(r,k)=>r[k]*1e6/r.frequency;
 return {name,rows:a.length,valid:a.filter(r=>r.valid).length,verifiedReadback:persist.length,medianUs:median(a.map(r=>us(r,'ticks'))),p95Us:q(a.map(r=>us(r,'ticks')),.95),computeMedianUs:median(a.map(r=>us(r,'computeTicks'))),flushMedianUs:median(a.map(r=>us(r,'flushTicks'))),routes:Object.fromEntries([...new Set(a.map(r=>r.route))].map(k=>[k,a.filter(r=>r.route===k).length]))};
});
const comparisons=['warm','propagation'].map(base=>{const a=[...histories.values()].map(v=>v[base]/v.structural);return{base,histories:a.length,medianSpeedup:median(a),wins:a.filter(x=>x>1).length}});
const statuses=Object.fromEntries([...new Set([...models.values()].map(r=>r.status))].map(k=>[k,[...models.values()].filter(r=>r.status===k).length]));
console.log(JSON.stringify({rows:rows.length,valid:rows.filter(r=>r.valid).length,models:models.size,conclusive:[...models.values()].filter(r=>r.conclusive).length,statuses,histories:histories.size,summary,comparisons},null,2));
