import fs from 'node:fs';
const root=new URL('./',import.meta.url);
const med=a=>{const s=[...a].sort((a,b)=>a-b),i=s.length>>1;return s.length%2?s[i]:(s[i-1]+s[i])/2};
const out={};
for(const split of ['dev','holdout']){
 const rows=fs.readFileSync(new URL(split+'/raw.jsonl',root),'utf8').trim().split('\n').map(JSON.parse);
 const groups=new Map(),cacheRows=rows.filter(x=>x.cache), pairs=new Map();
 for(const r of rows){
  const key=[r.n,r.family,r.seed,r.revision].join('/');
  if(!pairs.has(key))pairs.set(key,{});
  pairs.get(key)[r.cache?'cache':'base']=r;
 }
 for(const {cache:c,base:b} of pairs.values()){
  const key=[c.n,c.family,c.hit?'hit':'miss'].join('/');
  const fullKey=[c.n,c.family,"full-history"].join("/");
  if(!groups.has(fullKey))groups.set(fullKey,new Map());
  const full=groups.get(fullKey);if(!full.has(c.seed))full.set(c.seed,[]);
  full.get(c.seed).push([b.ticks*1e6/b.frequency,c.ticks*1e6/c.frequency,b.bytes,c.bytes]);
  if(!groups.has(key))groups.set(key,new Map());
  const g=groups.get(key);if(!g.has(c.seed))g.set(c.seed,[]);
  g.get(c.seed).push([b.ticks*1e6/b.frequency,c.ticks*1e6/c.frequency,b.bytes,c.bytes]);
 }
 const summary=[];
 for(const [key,g] of groups){
  const aggregate=key.endsWith("/full-history")?a=>a.reduce((s,x)=>s+x,0):med;
  const histories=[...g.values()].map(a=>({base:aggregate(a.map(v=>v[0])),cache:aggregate(a.map(v=>v[1])),baseBytes:aggregate(a.map(v=>v[2])),cacheBytes:aggregate(a.map(v=>v[3]))}));
  const speed=histories.map(x=>x.base/x.cache);
  let rand=13721;const sample=()=>{rand=(Math.imul(rand,1664525)+1013904223)>>>0;return rand/2**32};
  const boots=Array.from({length:2000},()=>med(Array.from({length:speed.length},()=>speed[Math.floor(sample()*speed.length)]))).sort((a,b)=>a-b);
  summary.push({key,histories:histories.length,baselineMedianUs:med(histories.map(x=>x.base)),cacheMedianUs:med(histories.map(x=>x.cache)),pairedMedianSpeedup:med(speed),bootstrap95:[boots[50],boots[1950]],wins:speed.filter(x=>x>1).length,baselineMedianBytes:med(histories.map(x=>x.baseBytes)),cacheMedianBytes:med(histories.map(x=>x.cacheBytes))});
 }
 out[split]={rows:rows.length,valid:rows.filter(r=>r.valid).length,independentRows:rows.filter(r=>r.independent).length,histories:new Set(rows.map(r=>[r.n,r.family,r.seed].join('/'))).size,cacheHits:cacheRows.filter(r=>r.hit).length,cacheMisses:cacheRows.filter(r=>!r.hit).length,summary};
}
console.log(JSON.stringify(out,null,2));
