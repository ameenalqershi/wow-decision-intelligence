import fs from 'node:fs';
const rows=fs.readFileSync(new URL('study/raw.jsonl',import.meta.url),'utf8').trim().split('\n').map(JSON.parse);
const arms=['CP-SAT','native-assignment','native-certified','assignment-proof'],pairs=new Map(),histories=new Map();
for(const r of rows){const k=[r.k,r.family,r.seed,r.rev].join('/');if(!pairs.has(k))pairs.set(k,{});const p=pairs.get(k);if(p[r.arm])throw Error('duplicate');p[r.arm]=r;}
const all=[...pairs.values()];if(all.some(p=>arms.some(a=>!p[a]||p[a].digest!==p['CP-SAT'].digest)))throw Error('pair');
for(const p of all){const t=p['assignment-proof'];if(!t.proof||t.status!=='Optimal')throw Error('proof');for(const a of arms)if(p[a].status==='Optimal'&&p[a].value!==t.value)throw Error('objective');
const k=[t.k,t.family,t.seed].join('/');if(!histories.has(k))histories.set(k,[]);histories.get(k).push(p);}
const hs=[...histories.values()];if(hs.some(h=>h.length!==2))throw Error('history');
const q=(a,p)=>{if(!a.length)return null;const x=[...a].sort((a,b)=>a-b),j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const med=a=>q(a,.5);
function compare(base,list){const good=list.filter(h=>h.every(p=>p[base].status==='Optimal'));const ratios=good.map(h=>({seed:h[0]['assignment-proof'].seed,ratio:h.reduce((s,p)=>s+p[base].ticks,0)/h.reduce((s,p)=>s+p['assignment-proof'].ticks,0)}));
const groups=Map.groupBy(ratios,r=>r.seed),keys=[...groups.keys()];let state=13579;const rand=()=>{state=(Math.imul(state,1664525)+1013904223)>>>0;return state/4294967296};const bs=[];
if(keys.length>=12)for(let i=0;i<5000;i++){const sample=[];for(let j=0;j<keys.length;j++)sample.push(...groups.get(keys[Math.floor(rand()*keys.length)]).map(r=>r.ratio));bs.push(med(sample));}
const a=ratios.map(x=>x.ratio);return{base,histories:a.length,totalHistories:list.length,clusters:keys.length,medianSpeedup:med(a),ci95:bs.length?[q(bs,.025),q(bs,.975)]:null,wins:a.filter(v=>v>1).length};}
const compareAll=list=>arms.slice(0,3).map(a=>compare(a,list));
const summaries=arms.map(arm=>{const a=rows.filter(r=>r.arm===arm),t=a.map(r=>r.ticks*1e6/r.frequency);return{arm,rows:a.length,statuses:Object.fromEntries([...new Set(a.map(r=>r.status))].map(s=>[s,a.filter(r=>r.status===s).length])),proofs:a.filter(r=>r.proof).length,witnesses:a.filter(r=>r.witness).length,medianUs:med(t),p95Us:q(t,.95),p99Us:q(t,.99),medianBytes:med(a.map(r=>r.bytes)),medianEvidenceBytes:med(a.map(r=>r.evidenceBytes))};});
console.log(JSON.stringify({rows:rows.length,models:all.length,distinctDigests:new Set(rows.map(r=>r.digest)).size,histories:hs.length,summaries,comparisons:compareAll(hs),bySize:[...new Set(rows.map(r=>r.k))].map(k=>({k,comparisons:compareAll(hs.filter(h=>h[0]['assignment-proof'].k===k))})),byFamily:[...new Set(rows.map(r=>r.family))].map(family=>({family,comparisons:compareAll(hs.filter(h=>h[0]['assignment-proof'].family===family))}))},null,2));
