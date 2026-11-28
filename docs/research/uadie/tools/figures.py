"""Editable Mermaid and SVG pairs. Coordinates in portrait publication points."""
from pathlib import Path
from xml.sax.saxutils import escape
import math

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "diagrams"
# Codes are redundant with color for monochrome readers.
COLORS = {"I":"#23463f","P":"#224f75","T":"#326437","C":"#83551c","U":"#86404a"}
FIGURES = [
("overview","Decision path","مسار القرار",["I|Original model #16","C|Normalize #20","C|Partition #39","P|Reuse or solve #42","I|Verify #29","I|Evidence #35"],[(0,1),(1,2),(2,3),(3,4),(4,5)]),
("hypergraph","What couples two decisions?","متى يرتبط قراران؟",["C|Decision x","C|Shared resource row","C|Decision y","T|One connected block","C|Independent z","T|Second block"],[(0,1),(2,1),(1,3),(4,5)]),
("impact","A deleted bridge still matters","أثر حذف علاقة",["C|Old incidence #15","C|Deleted row","C|New incidence","P|Old + new traversal","P|Current partition #39","C|Recheck both ends"],[(0,3),(1,3),(2,3),(3,4),(4,5)]),
("reuse","A digest locates evidence","كيف نعتمد دليلاً سابقاً؟",["P|Digest lookup #40","C|Compare scope + bytes","C|Check proof kind #41","T|Transfer under theorem","C|Current primal check","U|Mismatch: recompute"],[(0,1),(1,2),(2,3),(3,4),(2,5)]),
("verification","Primal and bound evidence","دليل الصلاحية ودليل الحد",["I|Original snapshot #24","I|Candidate witness","I|Evaluate rows #29","I|Recompute objective","C|Check bound #30","I|Classify evidence #35"],[(0,2),(1,2),(2,3),(3,5),(4,5)]),
("fallback","Recovery on failed certification","الرجوع عند تعذر التصديق",["C|Partial result #42","C|Current proof check","T|Certified result","C|Full remaining budget","C|Global solve + check","U|Unknown at deadline"],[(0,1),(1,2),(1,3),(3,4),(4,5)]),
("budget","Resources across an attempt","توزيع موارد المحاولة",["C|Total envelope #32","C|Charge preparation","C|Reserve verification","P|Choose affordable arm","C|Enforce deadline","C|Classify partial work"],[(0,1),(1,2),(2,3),(3,4),(4,5)]),
("memory","Evidence lifetime","دورة حياة الدليل في الذاكرة",["I|Frozen revision #24","P|Scoped cache #41","C|Pin during check","C|Release reference","P|Evict by byte budget","I|Retain lineage #35"],[(0,1),(1,2),(2,3),(3,4),(3,5)]),
("concurrency","Concurrent revision update","تغيّر النسخة أثناء الحل",["C|Pin revision r","C|Compute witness","C|Verify against r","C|Compare current head","I|Record scoped evidence","U|Stale for new head"],[(0,1),(1,2),(2,3),(3,4),(3,5)]),
("portfolio","Eligibility precedes ranking","اختيار محرك مؤهل",["I|Typed requirements #16","I|Manifest #26","C|Capability check #31","P|Rank within budget","C|Run + verify #29","U|No compatible backend"],[(0,2),(1,2),(2,3),(3,4),(2,5)]),
("components","Module boundaries","حدود المكونات",["I|DecisionGraph #14","I|UDIR #16","C|Lowering #21","I|Solver contracts #26","I|Verifier #29","I|Evidence store #35"],[(0,1),(1,2),(2,3),(1,4),(3,4),(4,5)]),
("states","Evidence states","حالات النتيجة",["C|Pending","C|Candidate","I|Primal checked #29","C|Global proof #30","T|Optimum certified","U|Unsupported or unknown"],[(0,1),(1,2),(2,3),(3,4),(1,5)]),
("issues","Dependencies for evaluation","اعتماديات التقييم",["I|Study #38","P|Components #39","P|Reuse #40 + #41","P|Repair #42","P|Harness #44","P|Evaluation #45"],[(0,1),(1,3),(2,3),(3,5),(4,5)]),
("sequence","Messages for a changed revision","تتابع رسائل النسخة الجديدة",["I|Registry: snapshot","P|Planner: old/new diff","P|Cache: scoped proof","C|Worker: solve misses","I|Checker: evaluate","I|Store: append #35"],[(0,1),(1,2),(2,3),(3,4),(4,5)]),
("comparison","Local and shared edits","التغيير المحلي والمشترك",["C|Old blocks A and B","C|Edit price in A","P|Recheck A; test B key","C|New row uses A and B","T|Join A and B","C|Solve joined model"],[(0,1),(1,2),(0,3),(3,4),(4,5)]),
("repair","Bound closes a repair","متى يثبت الإصلاح الأمثلية؟",["P|Freeze exterior #42","C|Solve restricted model","C|Check global feasibility","T|Upper bound for min","C|Global lower bound","T|Equal bounds: optimal"],[(0,1),(1,2),(2,3),(3,5),(4,5)]),
]
def build():
    OUT.mkdir(exist_ok=True)
    for stem,en,ar,nodes,edges in FIGURES:
        m=['flowchart TD']
        svg=['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 227" role="img">',f'<title>{escape(en)}</title>']
        # Two parallel lanes of three steps. Connections cross the gutter.
        points=[(12,8),(212,8),(412,8),(12,146),(212,146),(412,146)]
        for j,node in enumerate(nodes):
            status,label=node.split('|')
            m.append(f'  n{j}["[{status}] {label}"]:::{status}')
        for a,b in edges:
            m.append(f'  n{a} --> n{b}')
            x,y=points[a];xx,yy=points[b]
            if y==yy and abs(a-b)==1:
                d=1 if b>a else -1
                start=(x+(175 if d==1 else 0),y+31); end=(xx+(0 if d==1 else 175),yy+31)
                path=[start,end]
            else:
                offset=79+((a*3+b)%6)*9
                path=[(x+74,y+(62 if y<100 else 0)),(x+74,offset),(xx+104,offset),(xx+104,yy+(62 if yy<100 else 0))]
            svg.append('<polyline points="'+' '.join(f'{u},{v}' for u,v in path)+'" fill="none" stroke="#617385" stroke-width="2"/>')
            (u,v),(e,f)=path[-2:];theta=math.atan2(f-v,e-u)
            ends=[(e,f)]+[(e-9*math.cos(theta+t),f-9*math.sin(theta+t)) for t in (-.45,.45)]
            svg.append('<polygon points="'+' '.join(f'{u:.2f},{v:.2f}' for u,v in ends)+'" fill="#617385"/>')
        for (x,y),node in zip(points,nodes):
            status,label=node.split('|')
            svg.append(f'<rect x="{x}" y="{y}" width="175" height="62" rx="5" fill="{COLORS[status]}"/>')
            svg.append(f'<text x="{x+10}" y="{y+19}" fill="white" font-family="DejaVu Sans" font-size="11">{status}</text>')
            svg.append(f'<text x="{x+10}" y="{y+44}" fill="white" font-family="DejaVu Sans" font-size="12">{escape(label)}</text>')
        for code,color in COLORS.items(): m.append(f'  classDef {code} fill:{color},color:#fff,stroke:{color}')
        if stem=='sequence':
            m=['sequenceDiagram','  participant R as Registry','  participant P as Planner','  participant C as Cache','  participant W as Worker','  participant V as Checker','  participant E as Store','  R->>P: Frozen revision','  P->>C: Scoped evidence request','  C-->>P: Miss or proof','  P->>W: Solve misses','  W->>V: Proposed witness','  V->>E: Append scoped outcome']
        if stem=='states':
            m=['stateDiagram-v2','  [*] --> Pending','  Pending --> Candidate','  Candidate --> PrimalChecked','  PrimalChecked --> GlobalProof','  GlobalProof --> Certified','  Candidate --> UnsupportedOrUnknown']
        (OUT/f'{stem}.mmd').write_text('\n'.join(m)+'\n')
        (OUT/f'{stem}.svg').write_text('\n'.join(svg+['</svg>'])+'\n')
if __name__=='__main__': build()
