import fs from 'node:fs';
const rows=fs.readFileSync(new URL('dev/raw.jsonl',import.meta.url),'utf8').trim().split('\n').map(JSON.parse);
const groups=new Map();
for(const r of rows){const k=[r.n,r.family,r.seed,r.rev].join('/');if(!groups.has(k))groups.set(k,{});groups.get(k)[r.arm]=r}
const candidates=[.125,.25,.5,.75,1.1].map(threshold=>{
 let ticks=0,cert=0;
 for(const x of groups.values()){const f=x.original.features,arm=f.Item1||f.Item2>=threshold?'cert-direct':'warm';const r=x[arm];if(r.status!=='Optimal')throw Error('development nonconclusive');ticks+=r.ticks;if(arm==='cert-direct')cert++;}
 return {threshold,ticks,cert};
}).sort((a,b)=>a.ticks-b.ticks||a.threshold-b.threshold);
console.log(JSON.stringify({developmentRows:rows.length,models:groups.size,selected:candidates[0].threshold,candidates},null,2));
