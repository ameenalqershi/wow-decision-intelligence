import fs from 'node:fs';
const root=new URL('./',import.meta.url);
const median=a=>{const s=[...a].sort((a,b)=>a-b),i=s.length>>1;return s.length%2?s[i]:(s[i-1]+s[i])/2};
let results={};
for(const split of ['dev','holdout']){
 const rows=fs.readFileSync(new URL(split+'/raw.jsonl',root),'utf8').trim().split('\n').map(JSON.parse);
 const groups=new Map();
 for(const r of rows){
  const k=r.n+'/'+r.family;
  if(!groups.has(k))groups.set(k,new Map());
  const group=groups.get(k);
  if(!group.has(r.seed))group.set(r.seed,{old:[],fast:[],oldBytes:[],fastBytes:[]});
  const g=group.get(r.seed),a=r.fast?'fast':'old';
  g[a].push(r.ticks*1e6/r.frequency);g[a+'Bytes'].push(r.bytes);
 }
 const summary=[];
 for(const [key,g] of groups){
  const a=[...g.values()].map(x=>({old:median(x.old),fast:median(x.fast),oldBytes:median(x.oldBytes),fastBytes:median(x.fastBytes)}));
  summary.push({key,models:a.length,baselineMedianUs:median(a.map(x=>x.old)),normalizedMedianUs:median(a.map(x=>x.fast)),pairedMedianSpeedup:median(a.map(x=>x.old/x.fast)),pairedWins:a.filter(x=>x.fast<x.old).length,baselineMedianBytes:median(a.map(x=>x.oldBytes)),normalizedMedianBytes:median(a.map(x=>x.fastBytes))});
 }
 results[split]={rows:rows.length,valid:rows.filter(x=>x.valid).length,independentRows:rows.filter(x=>x.independent).length,distinctModels:[...groups.values()].reduce((n,g)=>n+g.size,0),summary};
}
console.log(JSON.stringify(results,null,2));
