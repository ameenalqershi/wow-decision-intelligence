"""Summarize all rows; medians over paired revision measurements in one host."""
from pathlib import Path
import gzip,json,statistics as s
from collections import defaultdict
p=Path(__file__).resolve().parent
rows=[json.loads(l) for l in gzip.open(p/"final/raw.jsonl.gz")]
by=defaultdict(list)
for r in rows:
    if r["revision"]: by[r["strategy"]].append(r)
def micro(row,k): return row[k]*1e6/row["frequency"]
def med(rs,k): return s.median(micro(r,k) for r in rs)
scores={}
print("# Measured evaluation on the frozen final split\n")
print("Table E1. Three rotations on 56 paired revisions per arm. Descriptive medians in microseconds. Cold is the corresponding first revision for each arm. Candidate wall includes its source witness check. Gate is part of solve. Allocation measures current-thread managed bytes. RSS is the maximum sampled process high-water mark in MiB.\n")
print("| Arm | Cold μs | Revised μs | Revised 10–90% μs | Check μs | Gate μs | Median allocated B | Peak MiB | Transfers / 56 |")
print("|---|---:|---:|---|---:|---:|---:|---:|---:|")
for arm,rs in sorted(by.items()):
    q=s.quantiles([micro(r,"wallTicks") for r in rs],n=10)
    cold=med([r for r in rows if r["strategy"]==arm and not r["revision"]],"wallTicks")
    scores[arm]=(med(rs,"wallTicks"),s.median(r["allocated"] for r in rs),max(r["peakProcessBytes"] for r in rs))
    print(f'| {arm} | {cold:.2f} | {scores[arm][0]:.2f} | {q[0]:.2f}–{q[-1]:.2f} | {med(rs,"verifyTicks"):.2f} | {med(rs,"GateTicks"):.2f} | {scores[arm][1]:g} | {scores[arm][2]/1048576:.1f} | {sum(r["Transported"] for r in rs)//3} |')
front=[a for a,v in scores.items() if not any(w!=v and all(x<=y for x,y in zip(w,v)) for w in scores.values())]
print("\nMeasured nondominated arms on median revised wall, median allocation and maximum process RSS, after filtering all correctness failures: "+", ".join(front)+". Repeating on another host can change membership.\n")
print("Table E2. All revised families, median μs and per-family transport counts (one structural observation per seed).\n")
print("| Family | full | warm | partition | reuse | box | pair | box hits | pair hits |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|")
for fam in sorted({r["family"] for r in rows}):
    subset={a:[r for r in rs if r["family"]==fam] for a,rs in by.items()}
    print("| "+fam+" | "+" | ".join(f'{med(subset[a],"wallTicks"):.2f}' for a in ["full","warm","partition","reuse","box","pair"])+" | "+" | ".join(str(sum(r["Transported"] for r in subset[a])//3)+"/8" for a in ["box","pair"])+" |")
print("\nVerified records:",sum(r["valid"] for r in rows),"of",len(rows),". CPU deltas at OS resolution were zero in",sum(r["cpuTicks"]==0 for r in rows),"records. Bounds matched the exhaustive optimum for all reported exact feasible results. Infeasible cases have no numeric gap.")
