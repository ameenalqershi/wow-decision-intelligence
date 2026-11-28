"""Independent finite audit. Run with Python 3, no packages or network."""
from itertools import product
import json


def dot(left, right):
    return sum(a * b for a, b in zip(left, right))


def main():
    assignments = list(product(range(2), repeat=2))
    vectors = list(product((-1, 0, 1), repeat=2))
    counts = dict(bound_checks=0, certified_primal_equalities=0, strict_bounds=0)
    for old_mask in range(1, 16):
        old = [x for i, x in enumerate(assignments) if old_mask & (1 << i)]
        for new_mask in range(1, 16):
            if old_mask & new_mask != new_mask:
                continue
            new = [x for i, x in enumerate(assignments) if new_mask & (1 << i)]
            for cost, change in product(vectors, repeat=2):
                old_value = min(dot(cost, x) for x in old)
                new_value = min(dot(cost, x) + dot(change, x) for x in new)
                lower = old_value + sum(min(0, d) for d in change)
                assert lower <= new_value
                counts["bound_checks"] += 1
                counts["strict_bounds"] += lower < new_value
                for witness in new:
                    candidate = dot(cost, witness) + dot(change, witness)
                    if candidate == lower:
                        assert candidate == new_value
                        counts["certified_primal_equalities"] += 1
    # Check products, including empty factors, offsets and unattained choices.
    cases = 0
    for mx, my in product(range(4), repeat=2):
        for a, b, offset in product((-1, 0, 1), repeat=3):
            fx = [x for x in range(2) if mx & (1 << x)]
            fy = [y for y in range(2) if my & (1 << y)]
            joint = [offset + a*x + b*y for x, y in product(fx, fy)]
            if fx and fy:
                assert min(joint) == offset + min(a*x for x in fx) + min(b*y for y in fy)
            else:
                assert not joint
            cases += 1
    print(json.dumps(dict(product_cases=cases, **counts), indent=2))


if __name__ == "__main__":
    main()
