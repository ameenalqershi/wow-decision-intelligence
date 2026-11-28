import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
const root=path.resolve(import.meta.dirname,'../..');
const walk=d=>fs.readdirSync(d,{withFileTypes:true}).flatMap(e=>e.isDirectory()?walk(path.join(d,e.name)):[path.join(d,e.name)]);
const read=p=>{const bytes=fs.readFileSync(p);return (p.endsWith('.gz')?zlib.gunzipSync(bytes):bytes).toString().trim().split('\n').filter(Boolean).map(JSON.parse)};
const validate=(files)=>{const rows=files.flatMap(read);return {records:rows.length,valid:rows.filter(r=>r.valid===true).length,invalid:rows.filter(r=>r.valid===false).length,missingValidity:rows.filter(r=>typeof r.valid!=='boolean').length}};
const get=p=>JSON.parse(fs.readFileSync(path.join(root,p),'utf8'));
const reviews=path.join(root,'reviews'),exp=path.join(root,'experiments');
const data={schema:1,scope:"Separate denominators; not an overall success rate. Timed rows share models and arms.",original:validate(['first','second','final'].map(s=>path.join(exp,s,'raw.jsonl.gz'))),G3:validate(walk(path.join(exp,'parity-v1')).filter(x=>x.endsWith('.jsonl'))),G4:validate(walk(path.join(exp,'orientation-v1')).filter(x=>x.endsWith('.jsonl'))),historicalNativeCorrected:validate([path.join(root,'evidence/orientation-native/small.jsonl')]),historicalWide:validate([path.join(root,'evidence/orientation-native/wide.jsonl')]),parityFinite:get('experiments/parity-v1/finite-audit.json')};
for(const [key,dir] of [['G5','quota-normalization'],['G6','quota-revision-cache'],['G7','adaptive-decision'],['G8','propagation-search'],['G9','structural-admission'],['G10','compressed-propagation']])
 data[key]=validate(['dev','holdout'].map(s=>path.join(reviews,dir,s,'raw.jsonl')));
const n5=path.join(reviews,'quota-normalization/native'),n6=path.join(reviews,'cache-native-history');
for(const [key,dir] of [['G5',n5],['G6',n6]]){
 data[key+'Native']=validate([path.join(dir,'native.jsonl')]);
 data[key+'Native'].conclusive=read(path.join(dir,'native.jsonl')).filter(r=>r.conclusive===true).length;
 data[key+'Boundary']=validate([path.join(dir,'boundary.jsonl')]);
}
const cache=get('reviews/quota-revision-cache/summary.json');const g7=get('reviews/adaptive-decision/summary.json').holdout;
const g8=get('reviews/propagation-search/summary.json').holdout;
const a=g7.summary.find(x=>x.name==='adaptive');
data.metrics={G6HistoricalHits:{numerator:cache.dev.cacheHits+cache.holdout.cacheHits,denominator:cache.dev.cacheHits+cache.holdout.cacheHits+cache.dev.cacheMisses+cache.holdout.cacheMisses},G6NativeHistoryHits:{numerator:read(path.join(n6,'native.jsonl')).filter(x=>x.hit).length,denominator:read(path.join(n6,'native.jsonl')).length},G7Transport:{numerator:a.transported,denominator:a.eligibleRevisions},G7SolveAvoidanceAll:{numerator:a.transported,denominator:a.rows},G7WarmWins:{numerator:g7.comparisons.find(x=>x.name==='adaptive'&&x.base==='warm').wins,denominator:g7.histories},G8WarmWins:{numerator:g8.comparisons.find(x=>x.base==='warm').wins,denominator:g8.histories},G7Holdout:g7.summary,G8Holdout:g8.summary,partialRepair:null,optimalStrategySelection:null,productionQualified:false};
const g9=get('reviews/structural-admission/summary.json').holdout;
const n9=get('reviews/structural-native/summary.json');
data.G9Native={models:n9.models,conclusive:n9.conclusive,records:n9.rows,valid:n9.valid,statuses:n9.statuses};
data.G9Finite=get('reviews/structural-finite/result.json');
data.metrics.G9WarmWins={numerator:g9.comparisons.find(x=>x.name==='structural'&&x.base==='warm').wins,denominator:g9.histories};
data.metrics.G9PropagationWins={numerator:g9.comparisons.find(x=>x.name==='structural'&&x.base==='propagation').wins,denominator:g9.histories};
data.metrics.G9Routes=g9.summary.find(x=>x.name==='structural').routes;
data.metrics.G9Holdout=g9.summary;
data.metrics.G9Durable=n9;
const g10=get('reviews/compressed-propagation/summary.json').holdout;
const g11=get('reviews/wide-native-quotient/summary.json');
const g11audit=get('reviews/wide-native-quotient/adversarial/summary.json');
const g12=get('reviews/wide-native-quotient/adaptive/summary.json');
data.G11Native=g11;
data.G11Adversarial=g11audit;
data.G12Adaptive=g12;
data.metrics.G10Holdout=g10.summary;
data.metrics.G10Comparisons=g10.comparisons;
data.metrics.G10WarmWins={numerator:g10.comparisons.find(x=>x.name==='compressed'&&x.base==='warm').wins,denominator:g10.histories};
data.metrics.G10G9Wins={numerator:g10.comparisons.find(x=>x.name==='compressed'&&x.base==='structural').wins,denominator:g10.histories};
data.metrics.G11HoldoutWins={numerator:g11.holdout.summary.wins,denominator:g11.holdout.models};
data.metrics.G12OriginalWins={numerator:g12.all.winsOriginal,denominator:g12.all.models};
data.metrics.G12QuotientWins={numerator:g12.all.winsQuotient,denominator:g12.all.models};
const wide=path.join(reviews,'wide-native-quotient');
const g13=get('reviews/wide-native-quotient/incremental/summary.json');
const g14=get('reviews/wide-native-quotient/template/summary.json');
for(const [key,dir,splits] of [['G13','incremental',['dev','holdout','disk']],['G14','template',['holdout','scale']]]){
 const bySplit={};
 for(const split of splits){
  const rows=read(path.join(wide,dir,split,'raw.jsonl'));
  bySplit[split]={records:rows.length,optimal:rows.filter(r=>r.status==='Optimal').length,nonconclusive:rows.filter(r=>!['Optimal','Infeasible'].includes(r.status)).length,distinctDigests:new Set(rows.map(r=>r.digest)).size,verifiedWitnesses:rows.filter(r=>r.verifiedWitness).length};
 }
 data[key]={bySplit,comparison:key==='G13'?g13:g14,independentAudit:get('reviews/wide-native-quotient/'+dir+'/audit/summary.json')};
}
data.G14.scaleIndependentReplay=get('reviews/wide-native-quotient/template/scale-independent/summary.json');
data.metrics.G13PlanHits={numerator:g13.holdout.hits,denominator:g13.holdout.cacheEvaluations};
data.metrics.G13G12Wins={numerator:g13.holdout.comparisons.find(x=>x.base==='G12').wins,denominator:g13.holdout.histories};
const a14=g14.holdout.summaries.find(x=>x.arm==='G14');
data.metrics.G14TemplateHits={numerator:a14.templateHits,denominator:a14.quotientCalls};
data.metrics.G14G13Wins={numerator:g14.holdout.comparisons.find(x=>x.base==='G13').wins,denominator:g14.holdout.histories};
data.metrics.G14ScaleConclusive={numerator:g14.scale.summaries.find(x=>x.arm==='G14').statuses.Optimal,denominator:g14.scale.models};
const g15=get('reviews/wide-native-quotient/certified/summary.json');
const g15splits={};
for(const split of ['dev','holdout','disk']){
 const rows=read(path.join(wide,'certified',split,'raw.jsonl'));
 g15splits[split]={records:rows.length,optimal:rows.filter(r=>r.status==='Optimal').length,distinctDigests:new Set(rows.map(r=>r.digest)).size,verifiedWitnesses:rows.filter(r=>r.verifiedWitness).length};
}
data.G15={bySplit:g15splits,comparison:g15,calibration:get('reviews/wide-native-quotient/certified/calibration.json'),independentAudit:get('reviews/wide-native-quotient/certified/audit/summary.json'),industrial:get('reviews/wide-native-quotient/certified/industrial/summary.json')};
const a15=g15.holdout.summaries.find(x=>x.arm==='G15');
data.metrics.G15DirectSolve={numerator:a15.directProofs,denominator:a15.rows};
data.metrics.G15CertifiedTransform={numerator:a15.certificateChecks,denominator:a15.rows};
data.metrics.G15G13Wins={numerator:g15.holdout.comparisons.find(x=>x.base==='G13').wins,denominator:g15.holdout.histories};
data.metrics.G15TamperRejection={numerator:data.G15.independentAudit.tamper-data.G15.independentAudit.acceptedTamper,denominator:data.G15.independentAudit.tamper};
const g16=get('reviews/wide-native-quotient/proof-reuse/summary.json');
const g16splits={};
for(const split of ['holdout','disk']){
 const rows=read(path.join(wide,'proof-reuse',split,'raw.jsonl'));
 g16splits[split]={records:rows.length,optimal:rows.filter(r=>r.status==='Optimal').length,distinctDigests:new Set(rows.map(r=>r.digest)).size,verifiedWitnesses:rows.filter(r=>r.verifiedWitness).length};
}
data.G16={bySplit:g16splits,comparison:g16,independentAudit:get('reviews/wide-native-quotient/proof-reuse/audit/summary.json'),industrial:get('reviews/wide-native-quotient/proof-reuse/industrial/summary.json'),readback:get('reviews/wide-native-quotient/proof-reuse/disk/readback.json'),storage:get('reviews/wide-native-quotient/proof-reuse/dedup/summary.json')};
const a16=g16.holdout.summaries.find(x=>x.arm==='G16'),r16=g16.holdout.summaries.find(x=>x.arm==='G16-reuse');
data.metrics.G16ProofHits={numerator:r16.hits,denominator:r16.rows};
data.metrics.G16AdaptiveEligibleHits={numerator:a16.hits,denominator:a16.certificateChecks};
data.metrics.G16AdaptiveAllHits={numerator:a16.hits,denominator:a16.rows};
data.metrics.G16DirectSolve={numerator:a16.directProofs,denominator:a16.rows};
data.metrics.G16G13Wins={numerator:g16.holdout.comparisons.find(x=>x.candidate==='G16'&&x.base==='G13').wins,denominator:g16.holdout.histories};
data.metrics.G16TamperRejection={numerator:data.G16.independentAudit.rejected,denominator:data.G16.independentAudit.tamper};
data.metrics.G16StorageByteReduction={numerator:data.G16.storage.full.bytes-data.G16.storage.compact.bytes,denominator:data.G16.storage.full.bytes};
data.G17={comparison:get('reviews/wide-native-quotient/assignment/summary.json'),independentAudit:get('reviews/wide-native-quotient/assignment/audit/summary.json')};
const aa=data.G17.comparison.summaries.find(x=>x.arm==='assignment-proof'),cp=data.G17.comparison.summaries.find(x=>x.arm==='CP-SAT');
data.metrics.G17DirectProven={numerator:aa.proofs,denominator:aa.rows};
data.metrics.G17CPOptimal={numerator:cp.statuses.Optimal,denominator:cp.rows};
data.metrics.G17TamperRejection={numerator:data.G17.independentAudit.rejected,denominator:data.G17.independentAudit.tamper};
const unified=get('reviews/wide-native-quotient/unified/summary.json'),selector=get('reviews/wide-native-quotient/unified/selector/summary.json'),gate=get('reviews/wide-native-quotient/unified/selector/gate-summary.json');
const unifiedSplits={};
for(const split of ['holdout','disk','selector']){
 const rows=read(path.join(wide,'unified',split,'raw.jsonl'));
 unifiedSplits[split]={records:rows.length,optimal:rows.filter(r=>r.status==='Optimal').length,distinctDigests:new Set(rows.map(r=>r.digest)).size,verifiedWitnesses:rows.filter(r=>r.witness).length};
}
data.G18G19={bySplit:unifiedSplits,comparison:unified,selector,gate,independentAudit:get('reviews/wide-native-quotient/unified/audit/summary.json'),adversarial:get('reviews/wide-native-quotient/unified/adversarial/summary.json'),readback:get('reviews/wide-native-quotient/unified/adversarial/readback.json'),journal:get('reviews/wide-native-quotient/unified/journal/summary.json'),industrial:get('reviews/wide-native-quotient/unified/industrial/summary.json')};
const u=unified.holdout.summaries.find(x=>x.arm==='unified-reuse'),g=selector.selector.summaries.find(x=>x.arm==='unified-gate');
data.metrics.G18FormalOptima={numerator:u.formal,denominator:u.rows};
data.metrics.G18AvoidGeneralCpSat={numerator:u.formal,denominator:u.rows};
data.metrics.G18ExactLocalHits={numerator:u.reused,denominator:u.blocks};
data.metrics.G18TransportedLocalProofs={numerator:u.transported,denominator:u.blocks};
data.metrics.G18OriginalWins={numerator:unified.holdout.comparisons.find(x=>x.base==='original').wins,denominator:unified.holdout.histories};
data.metrics.G19FlowFormal={numerator:u.routes.flow,denominator:120};
data.metrics.G18GateFormal={numerator:g.formal,denominator:g.rows};
data.metrics.G18JournalCrashRecovery={numerator:data.G18G19.journal.crashesPassed,denominator:data.G18G19.journal.crashes};
data.metrics.G18JournalTamperRejection={numerator:data.G18G19.journal.rejected,denominator:data.G18G19.journal.tamper};
const low=get('reviews/wide-native-quotient/low-overhead/summary.json'),weighted=get('reviews/wide-native-quotient/weighted/summary.json'),network=get('reviews/wide-native-quotient/general-flow/summary.json');
data.G20={comparison:low,audit:get('reviews/wide-native-quotient/low-overhead/audit/summary.json'),readback:get('reviews/wide-native-quotient/low-overhead/disk/readback.json')};
data.G21={comparison:weighted,audit:get('reviews/wide-native-quotient/weighted/audit/summary.json'),industrial:get('reviews/wide-native-quotient/weighted/industrial/summary.json')};
data.G22={comparison:network,audit:get('reviews/wide-native-quotient/general-flow/audit/summary.json')};
const a20=low.holdout.summaries.find(x=>x.arm==='G20'),a21=weighted.study.summaries.find(x=>x.arm==='G21'),a22=network.study.summaries.find(x=>x.arm==='G22');
data.metrics.G20FormalOptima={numerator:a20.formal,denominator:a20.rows};
data.metrics.G20JsonWins={numerator:low.holdout.comparisons.find(x=>x.candidate==='G20'&&x.base==='gate-json').wins,denominator:low.holdout.histories};
data.metrics.G20TypedWins={numerator:low.holdout.comparisons.find(x=>x.candidate==='G20'&&x.base==='gate-typed').wins,denominator:low.holdout.histories};
data.metrics.G21ArithmeticInfeasibility={numerator:a21.formal,denominator:a21.rows};
data.metrics.G21ProofRejection={numerator:data.G21.audit.tamper,denominator:data.G21.audit.tamper};
data.metrics.G21IndustrialAdmission={numerator:data.G21.industrial.supported,denominator:data.G21.industrial.records};
data.metrics.G22FormalOptimum={numerator:a22.statuses.Optimal,denominator:a22.rows};
data.metrics.G22FormalInfeasibility={numerator:a22.statuses.Infeasible,denominator:a22.rows};
data.metrics.G22ProofRejection={numerator:data.G22.audit.tamper,denominator:data.G22.audit.tamper};
console.log(JSON.stringify(data,null,2));
