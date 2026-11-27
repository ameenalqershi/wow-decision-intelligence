// Development-only review evidence. Structural checks are not approval.
import { readFileSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const read=p=>readFileSync(resolve(root,p),'utf8');
const hash=p=>createHash('sha256').update(readFileSync(resolve(root,p))).digest('hex');
const backlog=JSON.parse(read('planning/backlog.json'));
const requirements=JSON.parse(read('planning/requirements.json'));
const ids=Array.from({length:23},(_,i)=>'UI'+String(i+1).padStart(2,'0'));
const pilot=read('docs/design/pilot-contracts.md');
const ref='docs/design/references/01-WOW-DI-Studio.svg';
const modules=ids.map(id=>{
  const path='docs/design/studio/'+id+'.md', source=read(path);
  const links=[...source.matchAll(/\]\(([^)]+\.svg)\)/g)].map(m=>resolve(root,dirname(path),m[1]));
  const scope=[...source.matchAll(/^\| (UI\d+-\d+) \|/gm)].map(m=>m[1]);
  const gates=(source.match(/\*\*Implementation gates\.\*\* ([^.]+)\./)?.[1]??'').split(',').map(s=>s.trim());
  const ledger=backlog.find(x=>x.id===id).deps;
  const fields=[...new Set(source.match(/\bF-[A-Z]+\b/g)??[])];
  const ports=[...new Set(source.match(/\bAPI-[A-Z]+\b/g)??[])];
  const state=source.match(/stateDiagram-v2[\s\S]*?```/)?.[0]??'';
  const req=requirements.find(x=>x.issue===id);
  const actualFields=fields.filter(f=>pilot.includes('| '+f+' /'));
  const actualPorts=ports.filter(f=>pilot.includes('| '+f+' |'));
  return {id,sourceHash:hash(path),scope,wireAndVisual:links.length===2&&links.every(existsSync),
    svgViewBoxes:links.map(p=>existsSync(p)?readFileSync(p,'utf8').match(/viewBox="([^"]+)"/)?.[1]??null:null),
    fields,ports,unknownFields:fields.filter(f=>!actualFields.includes(f)),unknownPorts:ports.filter(f=>!actualPorts.includes(f)),
    ledgerOnly:ledger.filter(g=>!gates.includes(g)),specOnly:gates.filter(g=>!ledger.includes(g)),
    hasJourney:source.includes('**Operator journey.**'),hasFailure:source.includes('**Failure and recovery.**'),
    genericStateSkeleton:['Loading --> Ready','Loading --> Empty','Loading --> Failed','Failed --> Loading: Retry','Empty --> Loading: Change context'].every(x=>state.includes(x)),
    fieldScope:req.fieldScope,apiScope:req.apiScope};
});
const report={reference:ref,referenceHash:hash(ref),pilotHash:hash('docs/design/pilot-contracts.md'),modules};
console.log(JSON.stringify(report,null,2));
if(modules.some(m=>!m.wireAndVisual||!m.scope.length||m.unknownFields.length||m.unknownPorts.length))process.exitCode=1;
