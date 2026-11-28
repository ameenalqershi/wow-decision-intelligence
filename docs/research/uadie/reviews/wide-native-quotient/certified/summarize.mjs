import fs from 'node:fs';
const root=new URL('./',import.meta.url),arms=['original','warm','G13','G14','cert-native','cert-direct','G15'];
const q=(a,p)=>{if(!a.length)return null;const x=[...a].sort((a,b)=>a-b),j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const med=a=>q(a,.5),out={};
for(const split of ['holdout','disk']){
 const path=new URL(split+'/raw.jsonl',root);if(!fs.existsSync(path))continue;
 const rows=fs.readFileSync(path,'utf8').trim().split('\n').filter(Boolean).map(JSON.parse),pairs=new Map(),histories=new Map();
 for(const r of rows){const k=[r.n,r.family,r.seed,r.rev].join('/');if(!pairs.has(k))pairs.set(k,{});const a=pairs.get(k);if(a[r.arm])throw Error('duplicate');a[r.arm]=r;}
 const all=[...pairs.values()];for(const p of all){for(const a of Object.values(p))for(const b of Object.values(p)){if(a.digest!==b.digest)throw Error('digest');if(a.status==='Optimal'&&b.status==='Optimal'&&a.value!==b.value)throw Error('objective mismatch');}}
 const complete=all.filter(p=>arms.every(a=>p[a]));for(const p of complete){const r=p.G15,k=[r.n,r.family,r.seed].join('/');if(!histories.has(k))histories.set(k,[]);histories.get(k).push(p);}
 const hs=[...histories.values()].filter(h=>h.length===3),optimal=r=>r.status==='Optimal';
 const compare=(base,list)=>{
 const good=list.filter(h=>h.every(p=>optimal(p[base])&&optimal(p.G15)));
 const ratios=good.map(h=>({seed:h[0].G15.seed,ratio:h.reduce((s,p)=>s+p[base].ticks,0)/h.reduce((s,p)=>s+p.G15.ticks,0)}));
 const groups=Map.groupBy(ratios,r=>r.seed);const keys=[...groups.keys()];let state=13579;const rand=()=>{state=(Math.imul(state,1664525)+1013904223)>>>0;return state/4294967296};
 const bs=[];if(keys.length>=12)for(let i=0;i<5000;i++){const sample=[];for(let j=0;j<keys.length;j++)sample.push(...groups.get(keys[Math.floor(rand()*keys.length)]).map(r=>r.ratio));bs.push(med(sample));}
 const a=ratios.map(r=>r.ratio);
 return{base,histories:a.length,totalHistories:list.length,seedClusters:keys.length,medianSpeedup:med(a),ci95:bs.length?[q(bs,.025),q(bs,.975)]:null,p10:q(a,.1),p90:q(a,.9),wins:a.filter(x=>x>1).length};
 };
 const summaries=arms.map(arm=>{const a=rows.filter(r=>r.arm===arm),times=a.map(r=>r.ticks*1e6/r.frequency);return{arm,rows:a.length,statuses:Object.fromEntries([...new Set(a.map(r=>r.status))].map(s=>[s,a.filter(r=>r.status===s).length])),medianUs:med(times),p95Us:q(times,.95),p99Us:q(times,.99),medianBytes:med(a.map(r=>r.bytes)),medianEvidenceBytes:med(a.map(r=>r.evidenceBytes)),certificateChecks:a.filter(r=>r.certVerified).length,directProofs:a.filter(r=>r.directVerified).length,routes:Object.fromEntries([...new Set(a.map(r=>r.route))].map(s=>[s,a.filter(r=>r.route===s).length]))};});
 let readback=0;
 if(split==='disk'&&fs.existsSync(new URL('disk/g15-evidence.jsonl',root))){
 const d=fs.readFileSync(new URL('disk/g15-evidence.jsonl',root),'utf8').trim().split('\n').map(JSON.parse);if(d.length!==rows.length)throw Error('persistence count');
 for(let i=0;i<d.length;i++){const r=rows[i],e=d[i];if(r.digest!==e.digest||r.arm!==e.arm||r.rev!==e.rev||r.status!==e.Status||r.value!==e.Value||r.certVerified!==e.certVerified||r.directVerified!==e.directVerified)throw Error('persisted metadata');readback++;}
 }
 out[split]={rows:rows.length,revisionRecords:all.length,completeModels:complete.length,distinctDigests:new Set(rows.map(r=>r.digest)).size,allOptimal:complete.filter(p=>arms.every(a=>optimal(p[a]))).length,verifiedWitnesses:rows.filter(r=>r.verifiedWitness).length,histories:hs.length,readback,summaries,comparisons:arms.filter(a=>a!=='G15').map(a=>compare(a,hs)),byFamily:[...new Set(rows.map(r=>r.family))].map(f=>({family:f,comparisons:arms.filter(a=>a!=='G15').map(a=>compare(a,hs.filter(h=>h[0].G15.family===f)))}))};
}
console.log(JSON.stringify(out,null,2));
