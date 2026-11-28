import fs from 'node:fs';
const root=new URL('./',import.meta.url),arms=['original','G16','unified-mono','unified-reuse','unified-gate','unified-gate-flow'];
const q=(a,p)=>{if(!a.length)return null;const x=[...a].sort((a,b)=>a-b),j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const med=a=>q(a,.5),out={};
for(const split of ['selector']){
 const rows=fs.readFileSync(new URL(split+'/raw.jsonl',root),'utf8').trim().split('\n').map(JSON.parse),pairs=new Map(),histories=new Map();
 for(const r of rows){const k=[r.family,r.seed,r.rev].join('/');if(!pairs.has(k))pairs.set(k,{});const a=pairs.get(k);if(a[r.arm])throw Error('duplicate');a[r.arm]=r;}
 const all=[...pairs.values()];if(all.some(p=>arms.some(a=>!p[a]||p[a].digest!==p.original.digest)))throw Error('incomplete');
 for(const p of all){for(const a of arms)for(const b of arms)if(p[a].status==='Optimal'&&p[b].status==='Optimal'&&p[a].value!==p[b].value)throw Error('objective mismatch');
 const r=p['unified-gate-flow'],k=[r.family,r.seed].join('/');if(!histories.has(k))histories.set(k,[]);histories.get(k).push(p);}
 const hs=[...histories.values()];if(hs.some(h=>h.length!==5))throw Error('incomplete history');
 const compare=(base,list)=>{
 const good=list.filter(h=>h.every(p=>p['unified-gate-flow'].status==='Optimal'&&p[base].status==='Optimal'));
 const ratios=good.map(h=>({seed:h[0]['unified-gate-flow'].seed,ratio:h.reduce((s,p)=>s+p[base].ticks,0)/h.reduce((s,p)=>s+p['unified-gate-flow'].ticks,0)}));
 const groups=Map.groupBy(ratios,r=>r.seed),keys=[...groups.keys()];let state=13579;const rand=()=>{state=(Math.imul(state,1664525)+1013904223)>>>0;return state/4294967296};
 const bs=[];if(keys.length>=12)for(let i=0;i<5000;i++){const sample=[];for(let j=0;j<keys.length;j++)sample.push(...groups.get(keys[Math.floor(rand()*keys.length)]).map(r=>r.ratio));bs.push(med(sample));}
 const a=ratios.map(x=>x.ratio);return{base,histories:a.length,totalHistories:list.length,clusters:keys.length,medianSpeedup:med(a),ci95:bs.length?[q(bs,.025),q(bs,.975)]:null,wins:a.filter(x=>x>1).length,min:a.length?Math.min(...a):null,p10:q(a,.1),p90:q(a,.9)};
 };
 const summaries=arms.map(arm=>{const a=rows.filter(r=>r.arm===arm),times=a.map(r=>r.ticks*1e6/r.frequency);return{arm,rows:a.length,statuses:Object.fromEntries([...new Set(a.map(r=>r.status))].map(s=>[s,a.filter(r=>r.status===s).length])),medianUs:med(times),p95Us:q(times,.95),p99Us:q(times,.99),medianBytes:med(a.map(r=>r.bytes)),medianEvidenceBytes:med(a.map(r=>r.evidenceBytes)),formal:a.filter(r=>r.formal).length,witnesses:a.filter(r=>r.witness).length,reused:a.reduce((s,r)=>s+r.reused,0),transported:a.reduce((s,r)=>s+r.transported,0),rebuilt:a.reduce((s,r)=>s+r.rebuilt,0),blocks:a.reduce((s,r)=>s+r.blocks,0),routes:Object.fromEntries([...new Set(a.map(r=>r.route))].map(s=>[s,a.filter(r=>r.route===s).length]))};});
 out[split]={rows:rows.length,models:all.length,distinctDigests:new Set(rows.map(r=>r.digest)).size,allOptimal:all.filter(p=>arms.every(a=>p[a].status==='Optimal')).length,histories:hs.length,summaries,comparisons:arms.slice(0,-1).map(a=>compare(a,hs)),byFamily:[...new Set(rows.map(r=>r.family))].map(f=>({family:f,comparisons:arms.slice(0,-1).map(a=>compare(a,hs.filter(h=>h[0]['unified-gate-flow'].family===f))),routes:summaries.map(s=>({arm:s.arm,formal:rows.filter(r=>r.family===f&&r.arm===s.arm&&r.formal).length,reused:rows.filter(r=>r.family===f&&r.arm===s.arm).reduce((a,r)=>a+r.reused,0),transported:rows.filter(r=>r.family===f&&r.arm===s.arm).reduce((a,r)=>a+r.transported,0)}))}))};
}
console.log(JSON.stringify(out,null,2));
