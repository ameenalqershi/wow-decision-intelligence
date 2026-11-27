using System.Collections.Immutable;
using WowDi.Dml.Semantics;
using WowDi.Solver.Contracts;
using WowDi.Udir;

namespace WowDi.Dml.Lowering;

public enum NumericEncoding { Int64, Binary64ExactInput }
public enum TransformationKind { Exact, Approximate }
public sealed record NativeColumn(int Index,string SymbolId,ScalarKind Kind,string Lower,string Upper,SourceSpan Span);
public sealed record NativeCoefficient(int Column,string Value);
public sealed record NativeRow(int Index,string ConstraintId,Operation Relation,ImmutableArray<NativeCoefficient> Terms,
    string RightHandSide,string Scale,SourceSpan Span);
public sealed record NativeObjective(string ObjectiveId,Direction Direction,ImmutableArray<NativeCoefficient> Terms,
    string Offset,string Scale,SourceSpan Span);
public sealed record LoweringDiagnostic(string Code,string Message,SourceSpan? Span);
public sealed record LoweringResult(LoweredModel? Model,LoweringDiagnostic? Diagnostic)
{
    public bool Accepted=>Model is not null&&Diagnostic is null;
}
public sealed class LoweredModel
{
    public NormalizedModel Original {get;}
    public string OriginalDigest {get;}
    public string? FactsDigest {get;}
    public SolverCapabilities Manifest {get;}
    public NumericEncoding Encoding {get;}
    public TransformationKind Transformation=>TransformationKind.Exact;
    public ImmutableArray<NativeColumn> Columns {get;}
    public ImmutableArray<NativeRow> Rows {get;}
    public NativeObjective? Objective {get;}
    internal LoweredModel(NormalizedModel original,string originalDigest,string? factsDigest,SolverCapabilities manifest,
        NumericEncoding encoding,ImmutableArray<NativeColumn> columns,ImmutableArray<NativeRow> rows,NativeObjective? objective)
        =>(Original,OriginalDigest,FactsDigest,Manifest,Encoding,Columns,Rows,Objective)=
            (original,originalDigest,factsDigest,manifest,encoding,columns,rows,objective);
}
