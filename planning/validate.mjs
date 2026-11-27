import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';
const read = name => JSON.parse(readFileSync(new URL(name, import.meta.url), 'utf8'));
const rows = read('backlog.json');
const map = read('issue-map.json');
const index = new Map(rows.map(row => [row.id, row]));
assert.equal(index.size, rows.length, 'duplicate scope key');
assert.deepEqual(Object.keys(map).sort(), [...index.keys()].sort());
assert.equal(new Set(Object.values(map)).size, rows.length, 'duplicated issue');
const visited = new Set();
function visit(id, path = []) {
  assert(index.has(id), 'unknown dependency ' + id);
  assert(!path.includes(id), 'dependency cycle ' + [...path, id].join(' -> '));
  if (visited.has(id)) return;
  const row = index.get(id);
  assert(row.title && row.accept && row.area && Number.isSafeInteger(map[id]) && map[id] > 0);
  for (const dep of row.deps) visit(dep, [...path, id]);
  visited.add(id);
}
for (const id of index.keys()) visit(id);
console.log('Validated mapped issues and dependency graph:', visited.size);
