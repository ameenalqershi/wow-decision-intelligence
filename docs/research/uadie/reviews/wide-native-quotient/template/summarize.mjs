import fs from 'node:fs';
const root=new URL('./',import.meta.url),arms=['original','G11','G13','template-only','G14'];
const q=(a,p)=>{if(!a.length)return null;const x=[...a].sort((a,b)=>a-b),j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const med=a=>q(a,.5),out={};
for(const split of ['holdout','scale']){
 const rows=fs.readFileSync(new URL(split+'/raw.jsonl',root),'utf8').trim().split('\n').filter(Boolean).map(JSON.parse),pairs=new Map(),histories=new Map();
 for(const r of rows){const k=[r.n,r.family,r.seed,r.revision].join('/');if(!pairs.has(k))pairs.set(k,{});const a=pairs.get(k);if(a[r.arm])throw Error('duplicate');a[r.arm]=r;}
 const all=[...pairs.values()];if(all.some(p=>arms.some(a=>!p[a]||p[a].digest!==p.original.digest)))throw Error('incomplete/digest');
 for(const p of all){for(const a of arms)for(const b of arms)if(p[a].status==='Optimal'&&p[b].status==='Optimal'&&p[a].value!==p[b].value)throw Error('objective mismatch');
 const r=p.G14,k=[r.n,r.family,r.seed].join('/');if(!histories.has(k))histories.set(k,[]);histories.get(k).push(p);}
 const hs=[...histories.values()],optimal=r=>r.status==='Optimal';
 const compare=(base,list)=>{const good=list.filter(h=>h.length===(split==='scale'?3:4)&&h.every(p=>optimal(p[base])&&optimal(p.G14)));const ratio=good.map(h=>h.reduce((s,p)=>s+p[base].ticks,0)/h.reduce((s,p)=>s+p.G14.ticks,0));return{base,histories:good.length,totalHistories:list.length,medianSpeedup:med(ratio),p10:q(ratio,.1),p90:q(ratio,.9),wins:ratio.filter(x=>x>1).length}};
 const summaries=arms.map(arm=>{const a=rows.filter(r=>r.arm===arm),phases={};for(const name of new Set(a.flatMap(r=>Object.keys(r.phases)))){const s=a.filter(r=>Object.hasOwn(r.phases,name)).map(r=>r.phases[name]*1e6/r.frequency);phases[name]={calls:s.length,medianUs:med(s)};}const times=a.map(r=>r.ticks*1e6/r.frequency);return{arm,rows:a.length,statuses:Object.fromEntries([...new Set(a.map(r=>r.status))].map(s=>[s,a.filter(r=>r.status===s).length])),medianUs:med(times),p95Us:q(times,.95),p99Us:q(times,.99),medianBytes:med(a.map(r=>r.bytes)),hits:a.filter(r=>r.hit).length,templateHits:a.filter(r=>r.templateHit).length,quotientCalls:a.filter(r=>r.route==='quotient').length,routes:Object.fromEntries([...new Set(a.map(r=>r.route))].map(s=>[s,a.filter(r=>r.route===s).length])),phases};});
 out[split]={rows:rows.length,models:all.length,allOptimal:all.filter(p=>arms.every(a=>optimal(p[a]))).length,verifiedWitnesses:rows.filter(r=>r.verifiedWitness).length,histories:hs.length,summaries,comparisons:arms.filter(a=>a!=='G14').map(a=>compare(a,hs)),byFamily:[...new Set(rows.map(r=>r.family))].map(f=>({family:f,comparisons:arms.filter(a=>a!=='G14').map(a=>compare(a,hs.filter(h=>h[0].G14.family===f)))})),bySize:[...new Set(rows.map(r=>r.n))].map(n=>({n,comparisons:arms.filter(a=>a!=='G14').map(a=>compare(a,hs.filter(h=>h[0].G14.n===n))),status:Object.fromEntries(arms.map(a=>[a,Object.fromEntries([...new Set(rows.filter(r=>r.n===n&&r.arm===a).map(r=>r.status))].map(s=>[s,rows.filter(r=>r.n===n&&r.arm===a&&r.status===s).length]))]))}))};
}
console.log(JSON.stringify(out,null,2));
