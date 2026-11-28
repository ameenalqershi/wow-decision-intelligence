import fs from 'node:fs';
const root=new URL('./',import.meta.url);
const q=(a,p)=>{if(!a.length)return null;const x=[...a].sort((a,b)=>a-b),j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const out={};
for(const split of ['dev','holdout']){
 const rows=fs.readFileSync(new URL(split+'/raw.jsonl',root),'utf8').trim().split('\n').map(JSON.parse);
 const groups=new Map();
 for(const r of rows){const k=[r.n,r.family,r.seed,r.revision].join('/');if(!groups.has(k))groups.set(k,{});const a=groups.get(k);if(a[r.arm])throw Error('duplicate');a[r.arm]=r}
 const pairs=[...groups.values()];if(pairs.some(x=>!x.original||!x.quotient))throw Error('incomplete');
 const good=pairs.filter(x=>x.original.status==='Optimal'&&x.quotient.status==='Optimal');
 const conclusive=x=>['Optimal','Infeasible'].includes(x.status);
 const both=pairs.filter(x=>conclusive(x.original)&&conclusive(x.quotient));
 if(both.some(x=>x.original.status!==x.quotient.status||x.original.value!==x.quotient.value))throw Error('mismatch');
 const stat=a=>{const ratios=a.map(x=>x.original.ticks/x.quotient.ticks);return{models:a.length,medianSpeedup:q(ratios,.5),p10Speedup:q(ratios,.1),p90Speedup:q(ratios,.9),wins:ratios.filter(x=>x>1).length,originalMedianMs:q(a.map(x=>x.original.ticks*1e3/x.original.frequency),.5),quotientMedianMs:q(a.map(x=>x.quotient.ticks*1e3/x.quotient.frequency),.5),variableReductionMedian:q(a.map(x=>x.original.variables/Math.max(1,x.quotient.variables)),.5),medianReducedVariables:q(a.map(x=>x.quotient.variables),.5)}};
 const armStatus=Object.fromEntries(['original','quotient'].map(arm=>[arm,Object.fromEntries([...new Set(rows.filter(r=>r.arm===arm).map(r=>r.status))].map(s=>[s,rows.filter(r=>r.arm===arm&&r.status===s).length]))]));
 out[split]={rows:rows.length,models:pairs.length,bothConclusive:both.length,bothOptimal:good.length,matchingConclusive:both.length,armStatus,verifiedWitnesses:rows.filter(r=>r.verifiedWitness).length,summary:stat(good),bySize:[...new Set(rows.map(r=>r.n))].map(n=>({n,...stat(good.filter(x=>x.original.n===n))})),byFamily:[...new Set(rows.map(r=>r.family))].map(family=>({family,...stat(good.filter(x=>x.original.family===family))})),bySizeFamily:[...new Set(rows.map(r=>r.n))].flatMap(n=>[...new Set(rows.map(r=>r.family))].map(family=>({n,family,...stat(good.filter(x=>x.original.n===n&&x.original.family===family))})))};
}
console.log(JSON.stringify(out,null,2));
