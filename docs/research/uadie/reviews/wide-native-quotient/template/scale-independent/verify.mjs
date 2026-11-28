import fs from 'node:fs';
import zlib from 'node:zlib';
import crypto from 'node:crypto';
const dir=new URL('./',import.meta.url);
const rows=zlib.gunzipSync(fs.readFileSync(new URL('raw.jsonl.gz',dir))).toString().trim().split('\n').map(JSON.parse);
const prior=fs.readFileSync(new URL('../scale/raw.jsonl',dir),'utf8').trim().split('\n').map(JSON.parse).filter(r=>r.arm==='G14');
const key=r=>[r.n,r.family,r.revision].join('/');
const keys=new Map(prior.map(r=>[key(r),r]));
let matched=0,witnesses=0;
for(const r of rows){
 const p=keys.get(key(r)),m=r.model;
 if(!p||p.digest!==r.digest||p.value!==r.expected)throw Error('reference binding');
 if(crypto.createHash('sha256').update(JSON.stringify(m)).digest('hex').toUpperCase()!==r.digest)throw Error('source digest');
 if(r.status==='Optimal'){
  if(r.value!==r.expected||r.bits.length!==m.N||r.bits.some(v=>v!==0&&v!==1))throw Error('optimum/witness');
  for(const e of m.Edges)if(r.bits[e.Item1]+r.bits[e.Item2]!==1)throw Error('edge');
  for(const f of m.Fixes)if(r.bits[f.Item1]!==f.Item2)throw Error('fix');
  for(const c of m.Rows){const k=c.Scope.reduce((s,i)=>s+r.bits[i],0);if(k<c.L||k>c.U)throw Error('row');}
  if(m.Cost.reduce((s,v,i)=>s+v*r.bits[i],0)!==r.value)throw Error('cost');
  matched++;witnesses++;
 }
}
if(rows.length!==prior.length||new Set(rows.map(key)).size!==rows.length)throw Error('incomplete');
console.log(JSON.stringify({records:rows.length,distinctModelDigests:new Set(rows.map(r=>r.digest)).size,optimalMatches:matched,verifiedReadbackWitnesses:witnesses,bySize:[...new Set(rows.map(r=>r.n))].map(n=>({n,records:rows.filter(r=>r.n===n).length,optimal:rows.filter(r=>r.n===n&&r.status==='Optimal').length,distinctDigests:new Set(rows.filter(r=>r.n===n).map(r=>r.digest)).size}))},null,2));
