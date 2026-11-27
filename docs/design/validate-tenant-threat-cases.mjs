import fs from 'node:fs';
import assert from 'node:assert/strict';
const cases = JSON.parse(fs.readFileSync(new URL('./tenant-threat-cases.json', import.meta.url)));
const backlog = JSON.parse(fs.readFileSync(new URL('../../planning/backlog.json', import.meta.url)));
const known = new Set(backlog.map(x=>x.id));
const required = ['Identity','Imports','Plugins','Cache','Evidence export','External writes'];
const consumers = ['S11','I01','E01','E07','AI01','SEC01','SEC02'];
const ids = new Set();
for (const c of cases) {
  assert.match(c.id,/^F04-\d{2}$/);
  assert(!ids.has(c.id), 'duplicate '+c.id); ids.add(c.id);
  assert(required.includes(c.surface), 'unknown surface '+c.surface);
  assert(c.owners.length > 0 && c.owners.every(o=>known.has(o)), 'unknown owner '+c.id);
  assert(c.stimulus.trim() && c.oracle.trim(), 'missing stimulus/oracle '+c.id);
  assert.equal(c.status,'specified-not-executed');
}
for(const s of required) assert(cases.some(c=>c.surface===s),'missing surface '+s);
for(const o of consumers) assert(cases.some(c=>c.owners.includes(o)),'missing owner '+o);
console.log('F04 register: '+cases.length+' specified defensive cases; '+required.length+' threat surfaces; '+consumers.length+' implementation consumers traced. Runtime tests not executed.');
