import fs from 'node:fs';
const root=new URL('./',import.meta.url);
const read=f=>fs.readFileSync(new URL(f,root),'utf8').trim().split('\n').filter(Boolean).map(JSON.parse);
const q=(a,p)=>{if(!a.length)return null;const x=[...a].sort((a,b)=>a-b),j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const med=a=>q(a,.5),arms=['original','G11','G12','cold','G13'];
const out={};
for(const split of ['dev','holdout','disk']){
 const rows=read(split+'/raw.jsonl'),pairs=new Map(),histories=new Map();
 for(const r of rows){const k=[r.n,r.family,r.seed,r.revision].join('/');if(!pairs.has(k))pairs.set(k,{});const a=pairs.get(k);if(a[r.arm])throw Error('duplicate');a[r.arm]=r;}
 const all=[...pairs.values()];if(all.some(p=>arms.some(a=>!p[a]||p[a].digest!==p.original.digest)))throw Error('incomplete model/digest');
 for(const p of all)for(const a of arms)for(const b of arms)if(p[a].status==='Optimal'&&p[b].status==='Optimal'&&p[a].value!==p[b].value)throw Error('objective mismatch');
 const conclusive=p=>arms.every(a=>p[a].status==='Optimal'||p[a].status==='Infeasible');
 const good=all.filter(conclusive);
 for(const p of all){const r=p.G13,k=[r.n,r.family,r.seed].join('/');if(!histories.has(k))histories.set(k,[]);histories.get(k).push(p);}
 const hs=[...histories.values()].filter(h=>h.length===5&&h.every(conclusive));
 const compare=(base,list)=>{const ratio=list.map(h=>h.reduce((s,p)=>s+p[base].ticks,0)/h.reduce((s,p)=>s+p.G13.ticks,0));return{base,histories:ratio.length,medianSpeedup:med(ratio),p10:q(ratio,.1),p90:q(ratio,.9),wins:ratio.filter(x=>x>1).length}};
 const us=(r,t)=>r[t]*1e6/r.frequency;
 const summaries=arms.map(arm=>{const a=rows.filter(r=>r.arm===arm),phases={};for(const name of new Set(a.flatMap(r=>Object.keys(r.phases)))){const s=a.filter(r=>Object.hasOwn(r.phases,name)).map(r=>r.phases[name]*1e6/r.frequency);phases[name]={calls:s.length,medianUs:med(s),p95Us:q(s,.95)};}return{arm,rows:a.length,statuses:Object.fromEntries([...new Set(a.map(r=>r.status))].map(s=>[s,a.filter(r=>r.status===s).length])),medianUs:med(a.map(r=>us(r,'ticks'))),p95Us:q(a.map(r=>us(r,'ticks')),.95),p99Us:q(a.map(r=>us(r,'ticks')),.99),medianBytes:med(a.map(r=>r.bytes)),phases};});
 const hits=rows.filter(r=>r.arm==='G13'&&r.hit).length;
 let persisted=0;
 if(split==='disk'){const d=read('disk/g13-disk-evidence.jsonl');if(d.length!==rows.length)throw Error('persisted count');for(let i=0;i<d.length;i++){const r=rows[i],e=d[i];if(r.digest!==e.digest||r.arm!==e.arm||r.status!==e.Status||r.value!==e.Value||r.revision!==e.revision)throw Error('disk readback');}persisted=d.length;}
 out[split]={rows:rows.length,models:all.length,allConclusive:good.length,verifiedWitnesses:rows.filter(r=>r.verifiedWitness).length,histories:histories.size,completeConclusiveHistories:hs.length,hits,cacheEvaluations:all.length,persistedReadback:persisted,summaries,comparisons:arms.filter(a=>a!=='G13').map(a=>compare(a,hs)),byFamily:[...new Set(rows.map(r=>r.family))].map(f=>({family:f,comparisons:arms.filter(a=>a!=='G13').map(a=>compare(a,hs.filter(h=>h[0].G13.family===f)))})),bySize:[...new Set(rows.map(r=>r.n))].map(n=>({n,comparisons:arms.filter(a=>a!=='G13').map(a=>compare(a,hs.filter(h=>h[0].G13.n===n)))}))};
}
console.log(JSON.stringify(out,null,2));
