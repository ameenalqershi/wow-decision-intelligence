using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Independent;

namespace WowDi.Verification.Bounds;

// Host-owned binding. The observation must be associated with the actual admitted
// run by the trusted host, not populated from the adapter's untrusted response.
public sealed record EvidenceBinding(VerificationScope Scope,string? FactsSha256,string CatalogSha256);
public sealed record BoundObservation(EvidenceBinding Binding,SolverResult Result);
public sealed record MeasurementSnapshot(ImmutableArray<UnitDefinition> Units,ImmutableArray<string> Currencies,ImmutableArray<string> Calendars);
public enum EvidenceStrength { None, SolverAttestation, IndependentlyVerifiedPrimal, ExactEnumeration }
public sealed record EnumerationProof(int Checked,int Feasible,string? Optimum,ImmutableDictionary<string,string>? Witness)
{
    public string Algorithm => "wowdi.s05.enumeration/1";
}
public sealed record BoundAssessment(EvidenceBinding Binding,bool Rejected,string? Diagnostic,MathematicalStatus Status,
    Termination Termination,EvidenceStrength Strength,string? Incumbent,string? Bound,EvidenceStrength BoundStrength,
    string? AbsoluteGap,string? RelativeGap,EnumerationProof? Proof,
    MathematicalStatus ReportedStatus,Evidence? ReportedStatusEvidence,string? ObjectiveId,Direction? Direction,string? Unit,string? Currency,
    string? AbsoluteTolerance,string? RelativeTolerance,SolverIdentity Solver,ObjectiveBound? ReportedBound);

public static class EvidenceClassifier
{
    private const int MaximumAssignments=4096;
    private const int MaximumWork=1000000;
    private static readonly JsonSerializerOptions JsonOptions=new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,AllowDuplicateProperties=false,
        RespectNullableAnnotations=true,RespectRequiredConstructorParameters=true,MaxDepth=8,
        UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    public static BoundAssessment Assess(UdirDocument document,EvidenceBinding expected,ReadOnlyMemory<byte> catalogBytes,
        ReadOnlyMemory<byte>? factsBytes,BoundObservation observation,SolverCapabilities manifest,
        bool enumerate=false,int maximumAssignments=MaximumAssignments,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(document);ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observation);ArgumentNullException.ThrowIfNull(manifest);
        var r=observation.Result;
        var scope=expected.Scope;
        BoundAssessment Reject(string code)=>new(expected,true,code,MathematicalStatus.Unknown,r.Termination,EvidenceStrength.None,null,null,EvidenceStrength.None,null,null,null,r.MathematicalStatus,r.StatusEvidence,null,null,null,null,null,null,r.Solver,null);
        ct.ThrowIfCancellationRequested();
        try
        {
            if(observation.Binding!=expected||scope.TenantId!=document.Model.TenantId||scope.ModelId!=document.Model.ModelId||
                scope.RevisionId!=document.Model.RevisionId||scope.ModelSha256!=Hash(document.Write())||
                scope.RunId!=r.RunId||string.IsNullOrWhiteSpace(scope.RunId))return Reject("bounds.scope");
            if(catalogBytes.Length is <1 or >262144||factsBytes is {Length:>1048576})return Reject("bounds.snapshotSize");
            var catalog=catalogBytes.ToArray();
            var facts=factsBytes?.ToArray();
            if(Hash(catalog)!=expected.CatalogSha256||(facts is null?null:Hash(facts))!=expected.FactsSha256||
                facts is not null&&!document.Model.InputDigests.Contains(expected.FactsSha256!,StringComparer.Ordinal))
                return Reject("bounds.snapshot");
            var definitions=JsonSerializer.Deserialize<MeasurementSnapshot>(catalog,JsonOptions);
            if(definitions is null||definitions.Units.IsDefault||definitions.Currencies.IsDefault||definitions.Calendars.IsDefault||definitions.Units.Any(x=>x is null)||definitions.Currencies.Any(x=>x is null)||definitions.Calendars.Any(x=>x is null))
                return Reject("bounds.catalog");
            var measurements=new MeasurementSemantics(definitions.Units,definitions.Currencies,definitions.Calendars);
            document.ValidateMeasurements(measurements);
            r.ValidateAgainst(document,manifest);
            var model=document.Model;
            var types=model.Types.ToDictionary(t=>t.Id,t=>t.Kind,StringComparer.Ordinal);
            if(!manifest.MissingFeatures(document).IsEmpty||model.Symbols.Any(s=>s.Role==SymbolRole.Variable&&!manifest.Domains.Contains(types[s.TypeId])))
                return Reject("bounds.capabilities");
            // S01 lacks a machine-verifiable higher-priority stage context. Never
            // interpret a secondary bound as a global bound or combine soft penalties.
            if(model.Objectives.Length!=1||model.Constraints.Any(c=>!c.Hard)||!model.Scenarios.IsEmpty||
                model.Objectives[0].Weight!="1"||model.Requires.Any(c=>c!="integer.linear"))
                return Reject("bounds.unsupportedProfile");
            var objective=model.Objectives[0];
            var row=r.Objectives.Single();
            var objectiveType=model.Types.Single(t=>t.Id==model.Expressions.Single(e=>e.Id==objective.ExpressionId).TypeId);
            if(r.StatusEvidence?.Source==EvidenceSource.IndependentCertificate)return Reject("bounds.unverifiedCertificate");
            string? incumbent=null,bound=null;
            var strength=EvidenceStrength.None;
            var boundStrength=EvidenceStrength.None;
            if(r.Incumbent is not null)
            {
                var primal=OriginalVerifier.Check(document,measurements,scope,r,facts is null?null:new ReadOnlyMemory<byte>(facts),ct);
                if(!primal.Accepted)return Reject("bounds.invalidWitness");
                incumbent=primal.Objectives.Single().ExactValue;
                strength=EvidenceStrength.IndependentlyVerifiedPrimal;
            }
            // Do not trust a string claiming independent certification. An independent
            // certificate is recognized only by recomputing our supported proof below.
            if(row.Bound is { } reported)
            {
                if(reported.Evidence.Source!=EvidenceSource.SolverAttestation)return Reject("bounds.unverifiedCertificate");
                if(reported.Direction!=objective.Direction||incumbent is not null&&WrongOrder(reported.Value,incumbent,objective.Direction))
                    return Reject("bounds.direction");
                bound=reported.Value;boundStrength=EvidenceStrength.SolverAttestation;
            }
            EnumerationProof? proof=null;
            var status=incumbent is null?MathematicalStatus.Unknown:MathematicalStatus.Feasible;
            string? diagnostic=null;
            if(enumerate)
            {
                proof=Enumerate(document,measurements,scope,facts is null?null:new ReadOnlyMemory<byte>(facts),maximumAssignments,ct);
                if(proof is null)diagnostic="bounds.enumerationUnavailable";
                else if(proof.Feasible==0)
                {
                    if(incumbent is not null||r.MathematicalStatus is MathematicalStatus.Feasible or MathematicalStatus.Optimal or MathematicalStatus.Unbounded)
                        return Reject("bounds.proofConflict");
                    status=MathematicalStatus.Infeasible;strength=EvidenceStrength.ExactEnumeration;
                    bound=null;boundStrength=EvidenceStrength.None;
                }
                else
                {
                    if(r.MathematicalStatus is MathematicalStatus.Infeasible or MathematicalStatus.Unbounded||
                        bound is not null&&WrongOrder(bound,proof.Optimum!,objective.Direction))
                        return Reject("bounds.proofConflict");
                    bound=proof.Optimum;boundStrength=EvidenceStrength.ExactEnumeration;
                    if(incumbent is not null&&ExactValues.Compare(incumbent,bound!)==0)
                    {status=MathematicalStatus.Optimal;strength=EvidenceStrength.ExactEnumeration;}
                    else if(r.MathematicalStatus==MathematicalStatus.Optimal)
                        diagnostic="bounds.nonOptimalWitness";
                    else if(incumbent is null)
                    {
                        // The independently found witness belongs to this separate proof,
                        // not to the solver result. No fabricated incumbent or solver status.
                        diagnostic="bounds.independentWitnessAvailable";
                    }
                }
            }
            // A nonzero tolerance does not prove optimality; only exact enumeration does.
            string? absolute=null,relative=null;
            if(incumbent is not null&&bound is not null)
                (absolute,relative)=Gap(incumbent,bound);
            return new(expected,false,diagnostic,status,r.Termination,strength,incumbent,bound,boundStrength,absolute,relative,proof,r.MathematicalStatus,r.StatusEvidence,objective.Id,objective.Direction,objectiveType.Unit,objectiveType.Currency,row.AbsoluteTolerance,row.RelativeTolerance,r.Solver,row.Bound);
        }
        catch(ContractException){return Reject("bounds.contract");}
        catch(JsonException){return Reject("bounds.catalog");}
        catch(ArgumentException){return Reject("bounds.catalogOrContract");}
    }

    private static EnumerationProof? Enumerate(UdirDocument d,MeasurementSemantics catalog,VerificationScope scope,ReadOnlyMemory<byte>? facts,
        int maximum,CancellationToken ct)
    {
        if(maximum is <1 or >MaximumAssignments)throw new ArgumentException("Invalid enumeration budget.");
        var model=d.Model;
        var types=model.Types.ToDictionary(x=>x.Id,x=>x.Kind,StringComparer.Ordinal);
        var variables=model.Symbols.Where(s=>s.Role==SymbolRole.Variable).OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray();
        var domains=new List<string[]>();
        var total=1;
        foreach(var variable in variables)
        {
            ct.ThrowIfCancellationRequested();
            string[] domain;
            if(types[variable.TypeId]==ScalarKind.Boolean)domain=["false","true"];
            else if(types[variable.TypeId]==ScalarKind.Integer&&variable.Lower is not null&&variable.Upper is not null)
            {
                var lo=BigInteger.Parse(variable.Lower,CultureInfo.InvariantCulture);
                var hi=BigInteger.Parse(variable.Upper,CultureInfo.InvariantCulture);
                var size=hi-lo+1;
                if(size<1||size>maximum/total)return null;
                domain=Enumerable.Range(0,(int)size).Select(i=>(lo+i).ToString(CultureInfo.InvariantCulture)).ToArray();
            }
            else return null;
            if(domain.Length>maximum/total)return null;
            total*=domain.Length;domains.Add(domain);
        }
        var work=(long)total*(model.Expressions.Length+model.Symbols.Length+model.Constraints.Length+1);
        if(work>MaximumWork)return null;
        var objective=model.Objectives.Single();
        string? optimum=null;
        ImmutableDictionary<string,string>? witness=null;
        int feasible=0;
        for(int i=0;i<total;i++)
        {
            ct.ThrowIfCancellationRequested();
            var assignment=ImmutableDictionary.CreateBuilder<string,string>(StringComparer.Ordinal);
            int index=i;
            for(int j=0;j<variables.Length;j++)
            {assignment.Add(variables[j].Id,domains[j][index%domains[j].Length]);index/=domains[j].Length;}
            var candidate=new SolverResult(scope.TenantId,scope.ModelId,scope.RevisionId,scope.RunId,new("independent-enumeration","1","managed"),
                MathematicalStatus.Feasible,Termination.Completed,assignment.ToImmutable(),[new(objective.Id,"0",null,"0","0")],null,new(null,null,null));
            var check=OriginalVerifier.Check(d,catalog,scope,candidate,facts,ct);
            // Only a complete original-model evaluation with an actual hard-row violation
            // may eliminate a point. Arithmetic/profile/fact errors abort the entire proof.
            if(check.Constraints.Length!=model.Constraints.Length||check.Objectives.Length!=1||
                check.Diagnostics.Any(x=>x.Code is not ("verify.hardConstraint" or "verify.objectiveMismatch")))return null;
            if(check.Constraints.Any(c=>!c.Satisfied))continue;
            var value=check.Objectives.Single().ExactValue;
            var verified=OriginalVerifier.Check(d,catalog,scope,candidate with{Objectives=[new(objective.Id,value,null,"0","0")]},facts,ct);
            if(!verified.Accepted)return null;
            feasible++;
            if(optimum is null||Better(value,optimum,objective.Direction)){optimum=value;witness=candidate.Incumbent;}
        }
        return new(total,feasible,optimum,witness);
    }

    private static bool Better(string a,string b,Direction d)=>d==Direction.Minimize?ExactValues.Compare(a,b)<0:ExactValues.Compare(a,b)>0;
    private static bool WrongOrder(string bound,string incumbent,Direction d)=>d==Direction.Minimize?ExactValues.Compare(bound,incumbent)>0:ExactValues.Compare(bound,incumbent)<0;
    private static (string Absolute,string Relative) Gap(string incumbent,string bound)
    {
        static (BigInteger N,BigInteger D) Parts(string s)
        {var p=s.Split('/');return (BigInteger.Parse(p[0],CultureInfo.InvariantCulture),p.Length==1?BigInteger.One:BigInteger.Parse(p[1],CultureInfo.InvariantCulture));}
        static string Format(BigInteger n,BigInteger d)
        {var gcd=BigInteger.GreatestCommonDivisor(n,d);n/=gcd;d/=gcd;return d.IsOne?n.ToString(CultureInfo.InvariantCulture):FormattableString.Invariant($"{n}/{d}");}
        var a=Parts(incumbent);var b=Parts(bound);
        var n=BigInteger.Abs(a.N*b.D-b.N*a.D);var den=a.D*b.D;
        return (Format(n,den),Format(n*a.D,den*BigInteger.Max(a.D,BigInteger.Abs(a.N))));
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
