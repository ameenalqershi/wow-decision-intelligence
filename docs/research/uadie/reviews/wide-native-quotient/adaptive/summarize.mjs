import fs from 'node:fs';
import zlib from 'node:zlib';
const rows=zlib.gunzipSync(fs.readFileSync(new URL('raw.jsonl.gz',import.meta.url))).toString().trim().split('\n').map(JSON.parse);
const models=new Map(),sum=a=>a.reduce((s,x)=>s+x,0),arms=['original','quotient','adaptive'];
for(const r of rows){
 const k=[r.n,r.family,r.seed].join('/');if(!models.has(k))models.set(k,{});const p=models.get(k);if(p[r.arm])throw Error('duplicate');p[r.arm]=r;
 const m=r.model;if(r.bits!==null){if(r.bits.length!==m.N||r.bits.some(v=>v!==0&&v!==1))throw Error('domain');
 for(const e of m.Edges)if(r.bits[e.Item1]+r.bits[e.Item2]!==1)throw Error('edge');
 for(const f of m.Fixes)if(r.bits[f.Item1]!==f.Item2)throw Error('fix');
 for(const c of m.Rows){const v=sum(c.Scope.map(i=>r.bits[i]));if(v<c.L||v>c.U)throw Error('row');}
 if(sum(m.Cost.map((c,i)=>c*r.bits[i]))!==r.value)throw Error('cost');}
}
const all=[...models.values()],conclusive=r=>['Optimal','Infeasible'].includes(r.status);
for(const p of all){for(const a of arms){if(!p[a]||JSON.stringify(p[a].model)!==JSON.stringify(p.original.model))throw Error('incomplete/mismatched model');for(const b of arms){if(conclusive(p[a])&&conclusive(p[b])&&(p[a].status!==p[b].status||p[a].value!==p[b].value))throw Error('mismatch');}}}
const med=a=>{if(!a.length)return null;const x=[...a].sort((a,b)=>a-b);return(x[Math.floor((x.length-1)/2)]+x[Math.ceil((x.length-1)/2)])/2};
const stat=a=>{const b=a.filter(p=>arms.every(k=>conclusive(p[k])));const ratios=base=>b.map(p=>p[base].ticks/p.adaptive.ticks);
return{models:a.length,allConclusive:b.length,allOptimal:b.filter(p=>p.original.status==='Optimal').length,allInfeasible:b.filter(p=>p.original.status==='Infeasible').length,vsOriginal:med(ratios('original')),vsQuotient:med(ratios('quotient')),winsOriginal:ratios('original').filter(x=>x>1).length,winsQuotient:ratios('quotient').filter(x=>x>1).length,medianMs:Object.fromEntries(arms.map(k=>[k,med(b.map(p=>p[k].ticks*1e3/p[k].frequency))])),routes:Object.fromEntries([...new Set(a.map(p=>p.adaptive.route))].map(k=>[k,a.filter(p=>p.adaptive.route===k).length]))}};
const statuses=Object.fromEntries(arms.map(a=>[a,Object.fromEntries([...new Set(rows.filter(r=>r.arm===a).map(r=>r.status))].map(s=>[s,rows.filter(r=>r.arm===a&&r.status===s).length]))]));
console.log(JSON.stringify({rows:rows.length,witnesses:rows.filter(r=>r.bits!==null).length,statuses,all:stat(all),byFamily:[...new Set(rows.map(r=>r.family))].map(family=>({family,...stat(all.filter(p=>p.original.family===family))})),bySize:[...new Set(rows.map(r=>r.n))].map(n=>({n,...stat(all.filter(p=>p.original.n===n))}))},null,2));
