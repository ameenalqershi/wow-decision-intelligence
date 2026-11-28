import fs from 'node:fs';
const q=(a,p)=>{if(!a.length)return null;const x=[...a].sort((a,b)=>a-b),j=(x.length-1)*p;return x[Math.floor(j)]+(x[Math.ceil(j)]-x[Math.floor(j)])*(j%1)};
const med=a=>q(a,.5);
const cfg={dir:'../matrix-unified/',splits:['selector'],arms:['G24','preserve','preserve-reuse'],candidate:'preserve-reuse',revs:4,weighted:true};
const out={};
for(const split of cfg.splits){
 const rows=fs.readFileSync(new URL(cfg.dir+split+'/raw.jsonl',import.meta.url),'utf8').trim().split('\n').map(JSON.parse),pairs=new Map(),histories=new Map();
 for(const r of rows){const k=[r.n??'',r.family,r.seed,r.rev].join('/');if(!pairs.has(k))pairs.set(k,{});const p=pairs.get(k);if(p[r.arm])throw Error('duplicate');p[r.arm]=r;}
 const all=[...pairs.values()];for(const p of all){if(cfg.arms.some(a=>!p[a]||p[a].digest!==p[cfg.candidate].digest))throw Error('pair');for(const a of cfg.arms)for(const b of cfg.arms)if(p[a].status==='Optimal'&&p[b].status==='Optimal'&&p[a].value!==p[b].value)throw Error('objective');
 const r=p[cfg.candidate],k=[r.n??'',r.family,r.seed].join('/');if(!histories.has(k))histories.set(k,[]);histories.get(k).push(p);}
 const hs=[...histories.values()];if(hs.some(h=>h.length!==cfg.revs))throw Error('history');
 function compare(candidate,base,list){const good=list.filter(h=>h.every(p=>['Optimal','Infeasible'].includes(p[candidate].status)&&p[candidate].status===p[base].status));
 const ratios=good.map(h=>({seed:h[0][candidate].seed,ratio:h.reduce((s,p)=>s+p[base].ticks,0)/h.reduce((s,p)=>s+p[candidate].ticks,0)}));
 const groups=Map.groupBy(ratios,r=>r.seed),keys=[...groups.keys()];let state=13579;const rand=()=>{state=(Math.imul(state,1664525)+1013904223)>>>0;return state/4294967296};const bs=[];
 if(keys.length>=12)for(let i=0;i<5000;i++){const sample=[];for(let j=0;j<keys.length;j++)sample.push(...groups.get(keys[Math.floor(rand()*keys.length)]).map(r=>r.ratio));bs.push(med(sample));}
 const a=ratios.map(x=>x.ratio);return{candidate,base,histories:a.length,totalHistories:list.length,clusters:keys.length,medianSpeedup:med(a),ci95:bs.length?[q(bs,.025),q(bs,.975)]:null,wins:a.filter(v=>v>1).length,min:a.length?Math.min(...a):null};}
 const compares=list=>[...cfg.arms.filter(a=>a!==cfg.candidate).map(a=>compare(cfg.candidate,a,list)),...(!cfg.weighted?[compare('gate-typed','gate-json',list)]:[])];
 const summaries=cfg.arms.map(arm=>{const a=rows.filter(r=>r.arm===arm),t=a.map(r=>r.ticks*1e6/r.frequency),phases={};
 for(const name of new Set(a.flatMap(r=>Object.keys(r.phases)))){const s=a.filter(r=>Object.hasOwn(r.phases,name)).map(r=>r.phases[name]*1e6/r.frequency);phases[name]={calls:s.length,medianUs:med(s)};}
 return{arm,rows:a.length,statuses:Object.fromEntries([...new Set(a.map(r=>r.status))].map(s=>[s,a.filter(r=>r.status===s).length])),formal:a.filter(r=>r.formal||r.formalInfeasible).length,medianUs:med(t),p95Us:q(t,.95),p99Us:q(t,.99),medianBytes:med(a.map(r=>r.bytes)),medianEvidenceBytes:med(a.map(r=>r.evidenceBytes)),dropped:a.reduce((s,r)=>s+(r.dropped??0),0),gcdRows:a.reduce((s,r)=>s+(r.gcdRows??0),0),phases};});
 out[split]={rows:rows.length,models:all.length,distinctDigests:new Set(rows.map(r=>r.digest)).size,histories:hs.length,summaries,comparisons:compares(hs),byFamily:[...new Set(rows.map(r=>r.family))].map(f=>({family:f,comparisons:compares(hs.filter(h=>h[0][cfg.candidate].family===f))}))};
}
console.log(JSON.stringify(out,null,2));
