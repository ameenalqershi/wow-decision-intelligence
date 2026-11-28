import fs from 'node:fs';
const root=new URL('./',import.meta.url),arms=['original','hint','G13','G14','cert-native','cert-direct','G16-cold','G16-reuse','G16'];
const q=(a,p)=>{if(!a.length)return null;const x=[...a].sort((a,b)=>a-b),j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const med=a=>q(a,.5),out={};
for(const split of ['holdout','disk']){
 const rows=fs.readFileSync(new URL(split+'/raw.jsonl',root),'utf8').trim().split('\n').map(JSON.parse),pairs=new Map(),histories=new Map();
 for(const r of rows){const k=[r.n,r.family,r.seed,r.rev].join('/');if(!pairs.has(k))pairs.set(k,{});const a=pairs.get(k);if(a[r.arm])throw Error('duplicate');a[r.arm]=r;}
 const all=[...pairs.values()];if(all.some(p=>arms.some(a=>!p[a]||p[a].digest!==p.original.digest)))throw Error('incomplete model');
 for(const p of all){for(const a of arms)for(const b of arms)if(p[a].status==='Optimal'&&p[b].status==='Optimal'&&p[a].value!==p[b].value)throw Error('objective mismatch');
 const r=p.G16,k=[r.n,r.family,r.seed].join('/');if(!histories.has(k))histories.set(k,[]);histories.get(k).push(p);}
 const hs=[...histories.values()];if(hs.some(h=>h.length!==4))throw Error('incomplete history');
 const compare=(candidate,base,list)=>{
 const good=list.filter(h=>h.every(p=>p[candidate].status==='Optimal'&&p[base].status==='Optimal'));
 const ratios=good.map(h=>({seed:h[0][candidate].seed,ratio:h.reduce((s,p)=>s+p[base].ticks,0)/h.reduce((s,p)=>s+p[candidate].ticks,0)}));
 const groups=Map.groupBy(ratios,r=>r.seed),keys=[...groups.keys()];let state=13579;const rand=()=>{state=(Math.imul(state,1664525)+1013904223)>>>0;return state/4294967296};
 const bs=[];if(keys.length>=12)for(let i=0;i<5000;i++){const sample=[];for(let j=0;j<keys.length;j++)sample.push(...groups.get(keys[Math.floor(rand()*keys.length)]).map(r=>r.ratio));bs.push(med(sample));}
 const a=ratios.map(x=>x.ratio);return{candidate,base,histories:a.length,totalHistories:list.length,seedClusters:keys.length,medianSpeedup:med(a),ci95:bs.length?[q(bs,.025),q(bs,.975)]:null,wins:a.filter(x=>x>1).length,p10:q(a,.1),p90:q(a,.9)};
 };
 const comparisons=list=>[...arms.filter(a=>a!=='G16').map(a=>compare('G16',a,list)),compare('G16-reuse','G16-cold',list),compare('G16-reuse','cert-direct',list),compare('hint','original',list)];
 const summaries=arms.map(arm=>{const a=rows.filter(r=>r.arm===arm),times=a.map(r=>r.ticks*1e6/r.frequency),phases={};
 for(const name of new Set(a.flatMap(r=>Object.keys(r.phases)))){const s=a.filter(r=>Object.hasOwn(r.phases,name)).map(r=>r.phases[name]*1e6/r.frequency);phases[name]={calls:s.length,medianUs:med(s),p95Us:q(s,.95)};}
 return{arm,rows:a.length,statuses:Object.fromEntries([...new Set(a.map(r=>r.status))].map(s=>[s,a.filter(r=>r.status===s).length])),medianUs:med(times),p95Us:q(times,.95),p99Us:q(times,.99),medianBytes:med(a.map(r=>r.bytes)),medianEvidenceBytes:med(a.map(r=>r.evidenceBytes)),hits:a.filter(r=>r.hit).length,certificateChecks:a.filter(r=>r.certVerified).length,directProofs:a.filter(r=>r.directProof).length,routes:Object.fromEntries([...new Set(a.map(r=>r.route))].map(s=>[s,a.filter(r=>r.route===s).length])),phases};});
 out[split]={rows:rows.length,models:all.length,distinctDigests:new Set(rows.map(r=>r.digest)).size,allOptimal:all.filter(p=>arms.every(a=>p[a].status==='Optimal')).length,witnesses:rows.filter(r=>r.verifiedWitness).length,histories:hs.length,summaries,comparisons:comparisons(hs),byFamily:[...new Set(rows.map(r=>r.family))].map(f=>({family:f,comparisons:comparisons(hs.filter(h=>h[0].G16.family===f))}))};
}
console.log(JSON.stringify(out,null,2));
