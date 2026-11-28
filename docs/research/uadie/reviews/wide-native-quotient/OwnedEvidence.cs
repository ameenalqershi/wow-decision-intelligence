internal static class OwnedEvidence
{
    private static WideResult Copy(WideResult r)=>r with{Bits=r.Bits?.ToArray()};
    private static TransformCertificate Copy(TransformCertificate c)=>c with{Map=c.Map.ToArray(),Base=c.Base.ToArray(),Cost=c.Cost.ToArray(),Rows=c.Rows.Select(r=>r with{Index=r.Index.ToArray(),Coeff=r.Coeff.ToArray()}).ToArray()};
    private static AssignmentProof Copy(AssignmentProof p)=>p with{Shape=p.Shape with{LeftRows=p.Shape.LeftRows.ToArray(),RightRows=p.Shape.RightRows.ToArray(),Matrix=p.Shape.Matrix.ToArray()},Match=p.Match.ToArray(),U=p.U.ToArray(),V=p.V.ToArray()};
    private static FlowProof Copy(FlowProof p)=>p with{Left=p.Left.ToArray(),Right=p.Right.ToArray(),From=p.From.ToArray(),To=p.To.ToArray(),Potential=p.Potential.ToArray()};
    private static ProofOutcome Copy(ProofOutcome o)=>o with{Result=Copy(o.Result),Cert=o.Cert==null?null:Copy(o.Cert),Reduced=o.Reduced?.ToArray()};
    internal static UnifiedOutcome Copy(UnifiedOutcome o)=>o with{Result=Copy(o.Result),Blocks=o.Blocks.Select(b=>b with{Members=b.Members.ToArray(),Result=Copy(b.Result),Quotient=b.Quotient==null?null:Copy(b.Quotient),Assignment=b.Assignment==null?null:Copy(b.Assignment),Flow=b.Flow==null?null:Copy(b.Flow)}).ToArray()};
}
