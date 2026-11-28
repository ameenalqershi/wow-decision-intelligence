using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.Udir;

namespace WowDi.Optimization.Fingerprints;

public sealed record FingerprintContext(string CompilerVersion,string CompilerBuildSha256,string PlannerVersion,
    string VerifierVersion,string AdapterId,string AdapterVersion,string RuntimeId,string AdapterPackageSha256,
    string NumericProfile,string CatalogSha256,string? FactsSha256,ReadOnlyMemory<byte> Capabilities,ReadOnlyMemory<byte> Settings);

/// <summary>Digest is an index only. Equality requires exact versioned canonical bytes.</summary>
public sealed class FingerprintPart
{
    private readonly ImmutableArray<byte> bytes;
    public string Digest{get;}
    internal FingerprintPart(byte[] content){bytes=ImmutableArray.CreateRange(content);Digest=Hash(content);}
    internal static string Hash(ReadOnlySpan<byte> value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    public bool SameContent(FingerprintPart? other)=>other is not null&&Digest==other.Digest&&bytes.AsSpan().SequenceEqual(other.bytes.AsSpan());
}
public sealed record ReusePermissions(bool SameCanonicalInputsAndConfiguration,bool HintRequiresOriginalVerification,
    bool PlanRequiresHostRebind,bool CanTransferBound,bool CanTransferEvidence,string Reason);

/// <summary>Host-local semantic identities; never a source of authorization or mathematical proof.</summary>
public sealed class ModelFingerprints
{
    public const string Profile="wowdi.x03/1";
    private const int MaximumInputBytes=8*1024*1024;
    public string TenantId{get;}
    public string ModelId{get;}
    public string RevisionId{get;}
    public FingerprintPart Structure{get;}
    public FingerprintPart Data{get;}
    public FingerprintPart Constraints{get;}
    public FingerprintPart Objectives{get;}
    public FingerprintPart SolverConfiguration{get;}
    public FingerprintPart Provenance{get;}
    private FingerprintPart Domains{get;}
    private bool Supported{get;}

    private ModelFingerprints(Model m,FingerprintPart structure,FingerprintPart data,FingerprintPart constraints,
        FingerprintPart objectives,FingerprintPart config,FingerprintPart provenance,FingerprintPart domains,bool supported)
    {
        TenantId=m.TenantId;ModelId=m.ModelId;RevisionId=m.RevisionId;
        (Structure,Data,Constraints,Objectives,SolverConfiguration,Provenance,Domains,Supported)=
            (structure,data,constraints,objectives,config,provenance,domains,supported);
    }
    private static ContractException Bad(string code)=>new("fingerprint."+code,"Fingerprint admission failed: "+code);
    private static bool Digest(string? s)=>s is {Length:64}&&s.All(char.IsAsciiHexDigitLower);
    private static bool WellFormed(string? s)
    {
        if(s is null)return true;
        try{_=new UTF8Encoding(false,true).GetByteCount(s);return true;}
        catch(EncoderFallbackException){return false;}
    }
    private static bool Id(string? s)=>s is {Length:>0 and <=256}&&s==s.Trim()&&!s.Any(char.IsControl)&&WellFormed(s);
    private static IEnumerable<string?> ModelStrings(Model m)
    {
        yield return m.SemanticsVersion;
        yield return m.TimeBasis.CalendarId;yield return m.TimeBasis.TimeZoneId;yield return m.TimeBasis.Origin;yield return m.TimeBasis.TickUnit;
        foreach(var d in m.Sources)yield return d.Id;
        foreach(var t in m.Types){yield return t.Id;yield return t.Unit;yield return t.Currency;}
        foreach(var s in m.Symbols){yield return s.Id;yield return s.TypeId;yield return s.Lower;yield return s.Upper;yield return s.Span.SourceId;}
        foreach(var e in m.Expressions){yield return e.Id;yield return e.TypeId;yield return e.Literal;yield return e.SymbolId;yield return e.Span.SourceId;foreach(var a in e.Arguments)yield return a;}
        foreach(var r in m.Constraints){yield return r.Id;yield return r.PredicateId;yield return r.Penalty;yield return r.Span.SourceId;}
        foreach(var o in m.Objectives){yield return o.Id;yield return o.ExpressionId;yield return o.Weight;yield return o.Tolerance;yield return o.Span.SourceId;}
        foreach(var s in m.Scenarios){yield return s.Id;yield return s.InformationSet;yield return s.ParentId;yield return s.Probability;yield return s.Span.SourceId;foreach(var v in s.Overrides){yield return v.SymbolId;yield return v.Value;}}
        foreach(var r in m.Requires)yield return r;
    }
    private static T[] Order<T>(IEnumerable<T> items,Func<T,string> key)=>items.OrderBy(key,StringComparer.Ordinal).ToArray();

    /// <summary>Input bytes must come from an authorized immutable revision. Host supplies exact nonsecret catalog/configuration.</summary>
    public static ModelFingerprints Create(UdirDocument document,IReadOnlyDictionary<string,ReadOnlyMemory<byte>> inputs,
        FingerprintContext context,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(document);ArgumentNullException.ThrowIfNull(inputs);ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        var m=document.Model;
        if(document.MinorVersion!=0||document.Extensions is not null)throw Bad("unsupportedEnvelope");
        if(!ModelStrings(m).All(WellFormed))throw Bad("unicode");
        if(!Id(m.TenantId)||!Id(m.ModelId)||!Id(m.RevisionId)||
            !new[]{context.CompilerVersion,context.PlannerVersion,context.VerifierVersion,context.AdapterId,context.AdapterVersion,context.RuntimeId,context.NumericProfile}.All(Id)||
            !Digest(context.CompilerBuildSha256)||!Digest(context.AdapterPackageSha256)||!Digest(context.CatalogSha256)||
            context.FactsSha256 is not null&&!Digest(context.FactsSha256))throw Bad("context");
        if(!m.InputDigests.Contains(context.CatalogSha256,StringComparer.Ordinal)||
            context.FactsSha256 is not null&&!m.InputDigests.Contains(context.FactsSha256,StringComparer.Ordinal))throw Bad("snapshot");
        if(context.Capabilities.Length is <1 or >262144||context.Settings.Length is <1 or >65536)throw Bad("configSize");
        var capabilities=context.Capabilities.ToArray();var settings=context.Settings.ToArray();
        var expected=m.InputDigests.Concat(m.Sources.Select(s=>s.Sha256)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if(expected.Length>1024||inputs.Count!=expected.Length)throw Bad("inputs");
        // Re-index ordinally to avoid a caller's case-insensitive lookup changing identity.
        var supplied=inputs.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        var snapshots=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        long total=0;
        foreach(var digest in expected)
        {
            ct.ThrowIfCancellationRequested();
            if(!supplied.TryGetValue(digest,out var memory)||memory.Length>MaximumInputBytes-total)throw Bad("inputSizeOrMissing");
            var b=memory.ToArray();total+=b.Length;
            if(FingerprintPart.Hash(b)!=digest)throw Bad("inputDigest");
            snapshots.Add(digest,b);
        }
        foreach(var s in m.Sources)
        {
            ct.ThrowIfCancellationRequested();
            try{if(new UTF8Encoding(false,true).GetCharCount(snapshots[s.Sha256])!=s.Utf16Length)throw Bad("sourceLength");}
            catch(DecoderFallbackException){throw Bad("sourceEncoding");}
        }

        // Versioned private canonical JSON with fixed field order and ordinal sorting
        // of ID-keyed sets. Operand order and objective priority are semantic.
        FingerprintPart Part<T>(string kind,T content)
        {
            ct.ThrowIfCancellationRequested();
            return new(JsonSerializer.SerializeToUtf8Bytes(new {profile=Profile,kind,tenant=m.TenantId,model=m.ModelId,content}));
        }
        var types=Order(m.Types,t=>t.Id);
        var typesById=types.ToDictionary(t=>t.Id,StringComparer.Ordinal);
        var symbols=Order(m.Symbols,s=>s.Id);
        var expressions=Order(m.Expressions,e=>e.Id);
        var rows=Order(m.Constraints,c=>c.Id);
        var goals=m.Objectives.OrderBy(o=>o.Priority).ToArray();
        var scenarios=Order(m.Scenarios,s=>s.Id);
        var structure=Part("structure",new
        {
            m.SemanticsVersion,m.TimeBasis,types,requires=m.Requires.Order(StringComparer.Ordinal).ToArray(),
            symbols=symbols.Select(s=>new{s.Id,s.Role,s.TypeId}).ToArray(),
            expressions=expressions.Select(e=>new{e.Id,e.Op,e.TypeId,e.Arguments,e.SymbolId}).ToArray(),
            rows=rows.Select(r=>new{r.Id,r.PredicateId}).ToArray(),
            goals=goals.Select(o=>new{o.Id,o.ExpressionId,o.Priority}).ToArray(),
            scenarios=scenarios.Select(s=>new{s.Id,s.Stage,s.InformationSet,s.ParentId}).ToArray()
        });
        var data=Part("data",new
        {
            bounds=symbols.Select(s=>new{s.Id,s.Lower,s.Upper}).ToArray(),
            literals=expressions.Where(e=>e.Op==Operation.Literal).Select(e=>new{e.Id,e.Literal}).ToArray(),
            scenarioValues=scenarios.Select(s=>new{s.Id,s.Probability,overrides=Order(s.Overrides,o=>o.SymbolId)}).ToArray(),
            inputDigests=m.InputDigests.Order(StringComparer.Ordinal).ToArray(),
            inputs=m.InputDigests.Order(StringComparer.Ordinal).Select(d=>new{digest=d,bytes=snapshots[d]}).ToArray(),
            context.CatalogSha256,context.FactsSha256
        });
        // Include the actual reachable definitions in each semantic slice; a changed
        // coefficient/literal must invalidate its affected constraint/objective part.
        var nodes=expressions.ToDictionary(e=>e.Id,StringComparer.Ordinal);
        object Slice(IEnumerable<string> roots)
        {
            var seen=new HashSet<string>(StringComparer.Ordinal);var stack=new Stack<string>(roots);
            while(stack.TryPop(out var id))
            {
                ct.ThrowIfCancellationRequested();
                if(!seen.Add(id))continue;
                foreach(var a in nodes[id].Arguments)stack.Push(a);
            }
            return seen.Order(StringComparer.Ordinal).Select(id=>nodes[id]).Select(e=>new{e.Id,e.Op,e.TypeId,e.Arguments,e.Literal,e.SymbolId}).ToArray();
        }
        var constraints=Part("constraints",new{rows=rows.Select(r=>new{r.Id,r.PredicateId,r.Hard,r.Penalty}).ToArray(),
            expressions=Slice(rows.Select(r=>r.PredicateId))});
        var objectives=Part("objectives",new{goals=goals.Select(o=>new{o.Id,o.ExpressionId,o.Direction,o.Priority,o.Weight,o.Tolerance}).ToArray(),
            expressions=Slice(goals.Select(o=>o.ExpressionId))});
        var config=Part("configuration",new{context.CompilerVersion,context.CompilerBuildSha256,context.PlannerVersion,
            context.VerifierVersion,context.AdapterId,context.AdapterVersion,context.RuntimeId,context.AdapterPackageSha256,
            context.NumericProfile,capabilities,settings});
        var provenance=Part("provenance",new{m.RevisionId,document=document.Write(),
            sources=m.Sources.OrderBy(s=>s.Id,StringComparer.Ordinal).Select(s=>new{s.Id,bytes=snapshots[s.Sha256]}).ToArray()});
        var domains=Part("variable-domains",new{m.TimeBasis,variables=symbols.Where(s=>s.Role==SymbolRole.Variable)
            .Select(s=>new{s.Id,s.Lower,s.Upper,type=typesById[s.TypeId]}).ToArray()});
        var supported=m.Scenarios.IsEmpty&&m.Requires.All(f=>f is "integer.linear" or "rational.linear")&&m.Constraints.All(r=>r.Hard)&&
            m.Objectives.Length<=1&&m.Objectives.All(o=>o.Weight=="1"&&o.Tolerance=="0");
        return new(m,structure,data,constraints,objectives,config,provenance,domains,supported);
    }

    public ReusePermissions Compare(ModelFingerprints previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if(TenantId!=previous.TenantId||ModelId!=previous.ModelId)return new(false,false,false,false,false,"foreignScope");
        if(!Supported||!previous.Supported)return new(false,false,false,false,false,"unsupportedProfile");
        bool same=Structure.SameContent(previous.Structure)&&Data.SameContent(previous.Data)&&
            Constraints.SameContent(previous.Constraints)&&Objectives.SameContent(previous.Objectives)&&SolverConfiguration.SameContent(previous.SolverConfiguration);
        bool hint=Domains.SameContent(previous.Domains);
        // Even a same-revision cached plan is only a candidate, not a permission
        // to execute. Rebind and validate current source, facts, and adapter.
        bool plan=same&&Provenance.SameContent(previous.Provenance);
        return new(same,hint,plan,false,false,plan?"exactIdentityRebind":same?"sameProjectionNewProvenance":hint?"hintOnly":"changedDomains");
    }
}
