"""Independent finite-model audit of minimization bound transport. Python 3 stdlib."""
from itertools import product
import json

def verify():
    # States 0,1,2,3 denote 00, 01, 10, 11; costs deliberately need not be additive.
    states = range(4)
    subsets = [tuple(i for i in states if mask >> i & 1) for mask in range(1, 16)]
    functions = list(product((-1, 0, 1), repeat=4))
    checks = dict(restriction=0, transfer=0, restricted_witness=0)
    failures = []
    for original in subsets:
        for revised in subsets:
            if not set(revised) <= set(original):
                continue
            for f in functions:
                old_lb = min(f[i] for i in original)
                ref = min(f[i] for i in revised)
                assert old_lb <= ref
                checks['restriction'] += 1
                if any(f[i] == old_lb for i in revised):
                    assert old_lb == ref
                    checks['restricted_witness'] += 1
                for g in functions:
                    # Pointwise shift on surviving feasible assignments.
                    delta = min(g[i] - f[i] for i in revised)
                    lower = old_lb + delta
                    optimum = min(g[i] for i in revised)
                    checks['transfer'] += 1
                    if lower > optimum:
                        failures.append([original, revised, f, g])
    return dict(states=4, functions=len(functions), nonempty_subsets=len(subsets),
                checked=checks, failures=failures,
                relaxation_counterexample=dict(old=[1], revised=[0,1], costs=[0,1],
                                                old_lower=1, new_optimum=0))

if __name__ == '__main__':
    print(json.dumps(verify(), indent=2))
