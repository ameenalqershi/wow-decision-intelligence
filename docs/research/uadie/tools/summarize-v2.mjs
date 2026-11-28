// Offline research reporting only; no product dependency.
import fs from 'node:fs';
import path from 'node:path';
const root='docs/research/uadie';
const median=a=>{const b=a.slice().sort((a,b)=>a-b);return(b[(b.length-1)>>1]+b[b.length>>1])/2};
const quantile=(a,p)=>{const b=a.slice().sort((a,b)=>a-b);return b[Math.floor(p*(b.length-1))]};
const read=p=>fs.readFileSync(p,'utf8').trim().split('\n').filter(Boolean).map(JSON.parse);
const micro=(r,key)=>r[key]*1e6/r.frequency;
function bootstrap(rs,base){
 const by=new Map(base.map(r=>[r.family+':'+r.seed+':'+r.repeat,r]));
 const groups=new Map();
 for(const r of rs){const b=by.get(r.family+':'+r.seed+':'+r.repeat);if(!b)throw Error('Unpaired');const k=r.seed; if(!groups.has(k))groups.set(k,[]);groups.get(k).push(micro(r,'wallTicks')-micro(b,'wallTicks'))}
 const g=[...groups.values()], stats=[];let state=236;
 const rng=()=>{state=(Math.imul(1664525,state)+1013904223)>>>0;return state/4294967296};
 for(let b=0;b<2000;b++){let x=[];for(let j=0;j<g.length;j++)x.push(...g[Math.floor(rng()*g.length)]);stats.push(median(x));}
 return {pairedMedianDifferenceUs:median(g.flat()),clusterBootstrap95:[quantile(stats,.025),quantile(stats,.975)],clusters:g.length,draws:2000};
}
function report(dir){
 const rows=fs.readdirSync(dir).filter(x=>x.endsWith('.jsonl')).flatMap(x=>read(path.join(dir,x)));
 if(rows.some(r=>!r.valid))throw Error('Invalid results');
 const summary={records:rows.length,valid:rows.length,arms:{}};
 const warm=rows.filter(x=>x.strategy==='warm'&&x.revision===1);
 for(const arm of [...new Set(rows.map(x=>x.strategy))].sort()){
  const rs=rows.filter(x=>x.strategy===arm&&x.revision===1);
  const t=rs.map(x=>micro(x,'wallTicks'));
  const families={};
  for(const fam of [...new Set(rs.map(x=>x.family))]){
    const f=rs.filter(x=>x.family===fam);
    families[fam]={medianUs:median(f.map(x=>micro(x,'wallTicks'))),uniqueTransfers:new Set(f.filter(x=>x.Transported).map(x=>x.seed)).size,medianGateUs:median(f.map(x=>micro(x,'GateTicks')))};
  }
  summary.arms[arm]={medianUs:median(t),p10Us:quantile(t,.1),p90Us:quantile(t,.9),coldMedianUs:median(rows.filter(x=>x.strategy===arm&&x.revision===0).map(x=>micro(x,'wallTicks'))),verifyMedianUs:median(rs.map(x=>micro(x,'verifyTicks'))),auditMedianUs:median(rs.map(x=>micro(x,'auditTicks'))),gateMedianUs:median(rs.map(x=>micro(x,'GateTicks'))),cpuTicksMedian:median(rs.map(x=>x.cpuTicks)),allocatedMedian:median(rs.map(x=>x.allocated)),peakBytes:Math.max(...rs.map(x=>x.peakProcessBytes)),uniqueTransfers:new Set(rs.filter(x=>x.Transported).map(x=>x.family+':'+x.seed)).size,vsWarm:bootstrap(rs,warm),families};
 }
 const score=v=>[v.medianUs,v.allocatedMedian,v.peakBytes];
 summary.pareto=Object.keys(summary.arms).filter(k=>!Object.entries(summary.arms).some(([l,v])=>l!==k&&score(v).every((x,i)=>x<=score(summary.arms[k])[i])&&score(v).some((x,i)=>x<score(summary.arms[k])[i])));
 return summary;
}
for(const split of ['orientation-dev','orientation-holdout']){
 const dir=path.join(root,'experiments/orientation-v1',split), summary=report(dir);
 fs.writeFileSync(path.join(dir,'summary.json'),JSON.stringify(summary,null,2)+'\n',{flag:'wx'});
 console.log(split,summary.records,summary.pareto,Object.fromEntries(Object.entries(summary.arms).map(([k,v])=>[k,[v.medianUs,v.uniqueTransfers,v.allocatedMedian,v.vsWarm.clusterBootstrap95]])));
}
const d=path.join(root,'evidence/orientation-native');
const rows=read(path.join(d,'small.jsonl'));
const hold=rows.filter(r=>r.family!=='independent-history'&&r.revision===1);
const native={records:rows.length,valid:rows.filter(r=>r.valid).length,models:rows.length/10,holdoutRevised:{},wide:[],reportingError:[]};
const first=hold.filter(r=>r.arm==='full');
native.holdoutRevised['cp-sat-cold']=median(first.map(r=>micro(r,'coldTicks')));
native.holdoutRevised['cp-sat-hint']=median(first.map(r=>micro(r,'warmTicks')));
for(const arm of [...new Set(hold.map(r=>r.arm))])native.holdoutRevised[arm]=median(hold.filter(r=>r.arm===arm).map(r=>micro(r,'ticks')));
const wide=read(path.join(d,'wide.jsonl'));
for(const n of [...new Set(wide.map(r=>r.n))])for(const f of [...new Set(wide.map(r=>r.f))]){
 const r=wide.filter(x=>x.n===n&&x.f===f);
 native.wide.push({n,f,records:r.length,valid:r.filter(x=>x.valid).length,localMedianUs:median(r.map(x=>micro(x,'localTicks'))),nativeMedianUs:median(r.map(x=>micro(x,'nativeTicks')))});
}
for(const mode of ['small','wide']){
 const reports=fs.readFileSync(path.join(d,mode+'.stderr'),'utf8').split('\n').filter(x=>x.startsWith('{')).map(JSON.parse).filter(x=>x.kind==='binary64-report');
 native.reportingError.push({mode,count:reports.length,maxObjectiveError:Math.max(0,...reports.map(x=>x.objectiveError)),maxBoundError:Math.max(0,...reports.map(x=>x.boundError))});
}
fs.writeFileSync(path.join(d,'summary.json'),JSON.stringify(native,null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify(native,null,2));
