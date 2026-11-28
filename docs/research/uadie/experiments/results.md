# Measured evaluation on the frozen final split

Table E1. Three rotations on 56 paired revisions per arm. Descriptive medians in microseconds. Cold is the corresponding first revision for each arm. Candidate wall includes its source witness check. Gate is part of solve. Allocation measures current-thread managed bytes. RSS is the maximum sampled process high-water mark in MiB.

| Arm | Cold μs | Revised μs | Revised 10–90% μs | Check μs | Gate μs | Median allocated B | Peak MiB | Transfers / 56 |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| box | 75.09 | 70.38 | 20.46–279.12 | 2.96 | 15.78 | 9780 | 43.4 | 14 |
| full | 30.87 | 30.53 | 13.93–155.59 | 3.11 | 0.00 | 400 | 40.8 | 0 |
| pair | 65.08 | 57.91 | 18.26–265.78 | 2.81 | 14.45 | 8912 | 43.2 | 22 |
| partition | 71.31 | 79.22 | 54.50–328.21 | 3.23 | 0.00 | 12576 | 49.7 | 0 |
| reuse | 74.22 | 73.91 | 50.41–301.76 | 3.26 | 0.00 | 10084 | 49.6 | 0 |
| warm | 30.74 | 21.73 | 12.07–139.42 | 2.62 | 0.00 | 368 | 40.8 | 0 |

Measured nondominated arms on median revised wall, median allocation and maximum process RSS, after filtering all correctness failures: warm. Repeating on another host can change membership.

Table E2. All revised families, median μs and per-family transport counts (one structural observation per seed).

| Family | full | warm | partition | reuse | box | pair | box hits | pair hits |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| bridge | 33.49 | 19.60 | 78.53 | 68.30 | 20.58 | 21.78 | 6/8 | 6/8 |
| dense | 32.38 | 17.50 | 458.98 | 359.37 | 70.74 | 76.11 | 4/8 | 4/8 |
| flat-shift | 33.91 | 18.10 | 80.87 | 73.30 | 93.78 | 22.12 | 0/8 | 8/8 |
| global-price | 33.05 | 27.16 | 59.99 | 72.41 | 91.11 | 82.40 | 0/8 | 0/8 |
| outage | 27.65 | 21.81 | 85.83 | 61.59 | 40.77 | 37.20 | 4/8 | 4/8 |
| relax | 21.20 | 14.88 | 91.62 | 63.89 | 74.62 | 67.72 | 0/8 | 0/8 |
| shortage | 79.10 | 85.98 | 71.90 | 63.29 | 62.70 | 57.68 | 0/8 | 0/8 |

Verified records: 2016 of 2016 . CPU deltas at OS resolution were zero in 0 records. Bounds matched the exhaustive optimum for all reported exact feasible results. Infeasible cases have no numeric gap.
