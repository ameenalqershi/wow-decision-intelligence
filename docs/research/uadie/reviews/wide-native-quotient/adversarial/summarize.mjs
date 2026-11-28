import fs from 'node:fs';
import zlib from 'node:zlib';
const rows=zlib.gunzipSync(fs.readFileSync(new URL('raw.jsonl.gz',import.meta.url))).toString().trim().split('\n').map(JSON.parse);
const pairs=new Map();const count=a=>a.reduce((s,x)=>s+x,0);
for(const r of rows){
 const k=[r.n,r.family,r.seed].join('/');
 if(!pairs.has(k))pairs.set(k,{});
 const p=pairs.get(k);if(p[r.arm])throw Error('duplicate arm');p[r.arm]=r;
 const m=r.model;
 if(r.bits!==null){if(r.bits.length!==m.N||r.bits.some(x=>x!==0&&x!==1))throw Error('domain');
 for(const e of m.Edges)if(r.bits[e.Item1]+r.bits[e.Item2]!==1)throw Error('edge');
 for(const f of m.Fixes)if(r.bits[f.Item1]!==f.Item2)throw Error('fix');
 for(const c of m.Rows){const v=count(c.Scope.map(i=>r.bits[i]));if(v<c.L||v>c.U)throw Error('row');}
 if(count(m.Cost.map((c,i)=>c*r.bits[i]))!==r.value)throw Error('objective');
 }
}
const all=[...pairs.values()];
const conclusive=r=>['Optimal','Infeasible'].includes(r.status);
for(const p of all){if(!p.original||!p.quotient||JSON.stringify(p.original.model)!==JSON.stringify(p.quotient.model))throw Error('pair');
if(conclusive(p.original)&&conclusive(p.quotient)&&(p.original.status!==p.quotient.status||p.original.value!==p.quotient.value))throw Error('mismatch');}
const med=a=>{if(!a.length)return null;const v=[...a].sort((a,b)=>a-b);return (v[Math.floor((v.length-1)/2)]+v[Math.ceil((v.length-1)/2)])/2};
const stat=a=>{const b=a.filter(x=>conclusive(x.original)&&conclusive(x.quotient));const speed=b.map(x=>x.original.ticks/x.quotient.ticks);return{models:a.length,bothConclusive:b.length,bothOptimal:b.filter(x=>x.original.status==='Optimal').length,bothInfeasible:b.filter(x=>x.original.status==='Infeasible').length,medianSpeedup:med(speed),wins:speed.filter(x=>x>1).length,originalMedianMs:med(b.map(x=>x.original.ticks*1000/x.original.frequency)),quotientMedianMs:med(b.map(x=>x.quotient.ticks*1000/x.quotient.frequency)),medianReducedVars:med(b.map(x=>x.quotient.variables))}};
const status=Object.fromEntries(['original','quotient'].map(arm=>[arm,Object.fromEntries([...new Set(rows.filter(r=>r.arm===arm).map(r=>r.status))].map(s=>[s,rows.filter(r=>r.arm===arm&&r.status===s).length]))]));
console.log(JSON.stringify({rows:rows.length,verifiedWitnesses:rows.filter(r=>r.bits!==null).length,status,all:stat(all),byFamily:[...new Set(rows.map(r=>r.family))].map(family=>({family,...stat(all.filter(p=>p.original.family===family))})),bySize:[...new Set(rows.map(r=>r.n))].map(n=>({n,...stat(all.filter(p=>p.original.n===n))}))},null,2));
