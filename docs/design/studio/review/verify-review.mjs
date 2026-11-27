// Verify review deliverables, not feature behavior or human approval.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
import assert from 'node:assert/strict';
const dir=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(dir,'../../../..');
const run=(script,args=[])=>{
 const r=spawnSync(process.execPath,[path.join(dir,script),...args],{cwd:root,encoding:'utf8'});
 assert.equal(r.status,0,r.stderr);
 return r.stdout;
};
const matrixPath=path.join(dir,'modules/matrix.json');
const expected=fs.readFileSync(matrixPath,'utf8');
console.log(run('modules/build-matrix.mjs'));
assert.equal(fs.readFileSync(matrixPath,'utf8'),expected,'Matrix not regenerated against pinned source');
const matrix=JSON.parse(expected);
assert.equal(matrix.modules.length,23);
assert.equal(matrix.reviewCaseCount,120);
assert.equal(matrix.downstream.length,33);
const inventory=JSON.parse(run('check-sources.mjs'));
assert.equal(inventory.modules.length,23);
assert.ok(inventory.modules.every(m=>m.wireAndVisual&&!m.unknownFields.length&&!m.unknownPorts.length&&!m.ledgerOnly.length&&!m.specOnly.length));
let links=0;
function walk(p){
 for(const entry of fs.readdirSync(p,{withFileTypes:true})){
  const full=path.join(p,entry.name);
  if(entry.isDirectory())walk(full);
  else if(entry.name.endsWith('.md')){
   for(const match of fs.readFileSync(full,'utf8').matchAll(/\]\(([^)]+)\)/g)){
    const target=match[1].split('#')[0];
    if(!target||target.includes('://'))continue;
    assert.ok(fs.existsSync(path.resolve(path.dirname(full),target)),full+': '+target);links++;
   }
  }
 }
}
walk(dir);
const dependencies=JSON.parse(fs.readFileSync(path.join(dir,'modules/dependency-evidence.json'),'utf8'));
assert.equal(dependencies.length,24);
assert.ok(dependencies.every(d=>!d.wouldCycle));
console.log(JSON.stringify({modules:matrix.modules.length,requirements:matrix.reviewCaseCount,transitions:matrix.modules.reduce((n,m)=>n+m.transitions.length,0),downstream:matrix.downstream.length,links,reviewedCapabilityGates:dependencies.length,limitation:'Specification trace and structure only, not final design approval or feature execution.'},null,2));
