"""Independent exhaustive arithmetic audit of the disjoint-pair lower bound."""
from itertools import combinations, permutations, product
import json

values = list(product((0, 1), repeat=3))
deltas = list(product((-2, -1, 0, 1, 2), repeat=3))
edges = list(combinations(range(3), 2))
checks = strict = 0
for length in range(4):
    for rows in permutations(edges, length):
        feasible = [x for x in values if all(x[i]+x[j] == 1 for i,j in rows)]
        for delta in deltas:
            unused = set(range(3))
            floor = 0
            for i,j in rows:
                if {i,j} <= unused:
                    unused -= {i,j}
                    floor += min(delta[i],delta[j])
            floor += sum(min(0,delta[i]) for i in unused)
            exact = [sum(a*b for a,b in zip(x,delta)) for x in feasible]
            assert all(floor <= v for v in exact)
            checks += 1
            strict += bool(exact) and floor < min(exact)
print(json.dumps(dict(models_and_deltas=checks, strict_nonempty=strict), indent=2))
