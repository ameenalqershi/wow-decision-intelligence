using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WowDi.Contracts;
using WowDi.ModelRegistry;
using WowDi.Solver.Contracts;
using WowDi.Verification.Bounds;
using WowDi.Verification.Independent;

namespace WowDi.Verification.Evidence;

/// <summary>Host-produced invocation description, not executable instructions or credentials.</summary>
public sealed record RunInvocation(string AttemptId,string AdapterPackageSha256,string CompilerVersion,string PlannerVersion,
    string ProfileId,string VerifierBuildSha256,string RuntimeClass,string HardwareClass,int LogicalProcessors,long HostMemoryBytes,
    int Threads,long MemoryBudgetBytes,long TimeoutMs,int? Seed,DateTimeOffset StartedAt,DateTimeOffset FinishedAt,
    bool Enumerate,int MaximumAssignments);

public sealed record EvidenceManifest(EvidenceBinding Binding,RunInvocation Invocation,ImmutableArray<string> RevisionInputDigests,
    string CapabilitiesSha256,string SolverResultSha256,string AssessmentSha256,string SettingsSha256,
    string VerifierProfile,string? ParentEvidenceId);

/// <summary>Content-addressed exact evidence. Only constructed by recomputation or verified replay.
/// Authenticity and publication authority are the host's responsibility; hashes are not signatures.</summary>
public sealed class EvidenceRecord
{
    public const string ContractId="wowdi.evidence.run";
    public const string VerifierProfile="wowdi.s04/1+wowdi.s05/1";
    public const int MaximumArtifactBytes=1024*1024;
    private static readonly JsonSerializerOptions Options=new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,AllowDuplicateProperties=false,
        RespectNullableAnnotations=true,RespectRequiredConstructorParameters=true,MaxDepth=32,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        Converters={new JsonStringEnumConverter(allowIntegerValues:false)}
    };
    public EvidenceManifest Manifest{get;}
    public string EvidenceId{get;}
    public ImmutableArray<byte> ManifestBytes{get;}
    public ImmutableArray<byte> CatalogBytes{get;}
    public ImmutableArray<byte> CapabilitiesBytes{get;}
    public ImmutableArray<byte> SolverResultBytes{get;}
    public ImmutableArray<byte> AssessmentBytes{get;}
    public ImmutableArray<byte> SettingsBytes{get;}
    public BoundAssessment Assessment{get;}

    private EvidenceRecord(EvidenceManifest manifest,byte[] bytes,byte[] catalog,byte[] capabilities,
        byte[] result,byte[] assessment,byte[] settings,BoundAssessment checkedAssessment)
    {
        Manifest=manifest;EvidenceId=Hash(bytes);ManifestBytes=ImmutableArray.CreateRange(bytes);
        CatalogBytes=ImmutableArray.CreateRange(catalog);CapabilitiesBytes=ImmutableArray.CreateRange(capabilities);
        SolverResultBytes=ImmutableArray.CreateRange(result);AssessmentBytes=ImmutableArray.CreateRange(assessment);
        SettingsBytes=ImmutableArray.CreateRange(settings);Assessment=checkedAssessment;
    }

    /// <summary>Authorized audit view only. This is not SEC01's redacted external export.
    /// Includes scoped identifiers and detailed provenance, but not raw source values or assignments.</summary>
    public byte[] ExportAuditReport()=>Encode(new
    {
        contract="wowdi.evidence.audit",version="1.0",
        payload=new
        {
            evidenceId=EvidenceId,manifest=Manifest,solver=Assessment.Solver,
            reportedStatus=Assessment.ReportedStatus,termination=Assessment.Termination,
            assessedStatus=Assessment.Status,rejected=Assessment.Rejected,diagnostic=Assessment.Diagnostic,
            strength=Assessment.Strength,boundStrength=Assessment.BoundStrength,
            objectiveId=Assessment.ObjectiveId,direction=Assessment.Direction,unit=Assessment.Unit,currency=Assessment.Currency,
            incumbent=Assessment.Incumbent,bound=Assessment.Bound,absoluteGap=Assessment.AbsoluteGap,relativeGap=Assessment.RelativeGap,
            absoluteTolerance=Assessment.AbsoluteTolerance,relativeTolerance=Assessment.RelativeTolerance,
            proof=Assessment.Proof is null?null:new {Assessment.Proof.Algorithm,Assessment.Proof.Checked,Assessment.Proof.Feasible,Assessment.Proof.Optimum}
        }
    });

    // Caller obtains a real, authorized M11 or equivalent immutable revision. A host
    // selects the exact run binding and native result; an HTTP caller cannot supply authority.
    public static EvidenceRecord Capture(StoredRevision revision,EvidenceBinding binding,RunInvocation invocation,
        ReadOnlyMemory<byte> catalog,SolverCapabilities capabilities,SolverResult result,ReadOnlyMemory<byte> settings,
        string? parentEvidenceId=null,CancellationToken ct=default)
    {
        ValidateInvocation(invocation);
        ValidateParent(parentEvidenceId);
        var inputs=ValidateRevision(revision,binding,ct);
        var catalogCopy=Copy(catalog,262144);
        if(Hash(catalogCopy)!=binding.CatalogSha256)throw Error("catalogDigest");
        var settingCopy=Copy(settings,65536);
        ValidateSettings(settingCopy);
        var capabilityBytes=Encode(capabilities);
        var resultBytes=Encode(result);
        var frozenCapabilities=Decode<SolverCapabilities>(capabilityBytes);
        var frozenResult=Decode<SolverResult>(resultBytes);
        ValidateProducer(binding,frozenCapabilities,frozenResult,invocation);
        var assessment=EvidenceClassifier.Assess(revision.Document,binding,catalogCopy,FindFacts(revision,binding),
            new(binding,frozenResult),frozenCapabilities,invocation.Enumerate,invocation.MaximumAssignments,ct);
        // Evidence can retain a rejected observation for audit; it never grants authorization.
        var assessmentBytes=Encode(assessment);
        var manifest=new EvidenceManifest(binding,invocation,inputs,Hash(capabilityBytes),Hash(resultBytes),Hash(assessmentBytes),
            Hash(settingCopy),VerifierProfile,parentEvidenceId);
        var manifestBytes=Encode(new {contract=ContractId,version="1.0",payload=manifest});
        return new(manifest,manifestBytes,catalogCopy,capabilityBytes,resultBytes,assessmentBytes,settingCopy,assessment);
    }

    public static EvidenceRecord Replay(StoredRevision revision,string trustedTenant,string expectedEvidenceId,
        ReadOnlyMemory<byte> manifestBytes,ReadOnlyMemory<byte> catalogBytes,ReadOnlyMemory<byte> capabilitiesBytes,
        ReadOnlyMemory<byte> solverResultBytes,ReadOnlyMemory<byte> assessmentBytes,ReadOnlyMemory<byte> settingsBytes,
        CancellationToken ct=default)
    {
        var m=Copy(manifestBytes,MaximumArtifactBytes);
        if(!Digest(expectedEvidenceId)||Hash(m)!=expectedEvidenceId)throw Error("digest");
        var envelope=EnvelopeReader.Read(m,ContractId,1);
        EvidenceManifest manifest;
        try{manifest=envelope.Payload.Deserialize<EvidenceManifest>(Options)??throw Error("manifest");}
        catch(JsonException){throw Error("manifest");}
        if(manifest.VerifierProfile!=VerifierProfile||manifest.Binding.Scope.TenantId!=trustedTenant)
            throw Error("scopeOrVersion");
        ValidateInvocation(manifest.Invocation);ValidateParent(manifest.ParentEvidenceId);
        var inputs=ValidateRevision(revision,manifest.Binding,ct);
        if(manifest.RevisionInputDigests.IsDefault||!inputs.SequenceEqual(manifest.RevisionInputDigests,StringComparer.Ordinal))
            throw Error("lineage");
        var catalog=Copy(catalogBytes,262144);var caps=Copy(capabilitiesBytes,MaximumArtifactBytes);
        var result=Copy(solverResultBytes,MaximumArtifactBytes);var assessment=Copy(assessmentBytes,MaximumArtifactBytes);
        var settings=Copy(settingsBytes,65536);
        if(Hash(catalog)!=manifest.Binding.CatalogSha256||Hash(caps)!=manifest.CapabilitiesSha256||
            Hash(result)!=manifest.SolverResultSha256||Hash(assessment)!=manifest.AssessmentSha256||Hash(settings)!=manifest.SettingsSha256)
            throw Error("artifactDigest");
        ValidateSettings(settings);
        var decoded=Decode<SolverResult>(result);
        var capabilities=Decode<SolverCapabilities>(caps);
        ValidateProducer(manifest.Binding,capabilities,decoded,manifest.Invocation);
        var recomputed=EvidenceClassifier.Assess(revision.Document,manifest.Binding,catalog,FindFacts(revision,manifest.Binding),
            new(manifest.Binding,decoded),capabilities,manifest.Invocation.Enumerate,manifest.Invocation.MaximumAssignments,ct);
        if(!Encode(recomputed).AsSpan().SequenceEqual(assessment))throw Error("assessmentMismatch");
        return new(manifest,m,catalog,caps,result,assessment,settings,recomputed);
    }

    private static void ValidateProducer(EvidenceBinding binding,SolverCapabilities caps,SolverResult result,RunInvocation invocation)
    {
        var s=binding.Scope;
        if(result.TenantId!=s.TenantId||result.ModelId!=s.ModelId||result.RevisionId!=s.RevisionId||result.RunId!=s.RunId||
            result.Solver!=caps.Solver||!Enum.IsDefined(result.Termination)||!Enum.IsDefined(result.MathematicalStatus)||
            invocation.Threads>caps.MaxThreads)
            throw Error("producerScope");
    }

    private static ImmutableArray<string> ValidateRevision(StoredRevision revision,EvidenceBinding binding,CancellationToken ct)
    {
        var scope=binding.Scope;var model=revision.Document.Model;
        if(!Id(scope.TenantId)||!Id(scope.ModelId)||!Id(scope.RevisionId)||!Id(scope.RunId)||
            model.TenantId!=scope.TenantId||model.ModelId!=scope.ModelId||model.RevisionId!=scope.RevisionId||
            revision.Graph.Revision.TenantId!=scope.TenantId||revision.Graph.Revision.ModelId!=scope.ModelId||revision.Graph.Revision.RevisionId!=scope.RevisionId||
            scope.ModelSha256!=Hash(revision.Document.Write())||!Digest(binding.CatalogSha256)||
            binding.FactsSha256 is not null&&!Digest(binding.FactsSha256))
            throw Error("scope");
        var digests=model.InputDigests.Concat(model.Sources.Select(s=>s.Sha256)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if(digests.Length>1024||revision.Inputs.Count!=digests.Length||
            binding.FactsSha256 is not null&&!model.InputDigests.Contains(binding.FactsSha256,StringComparer.Ordinal))
            throw Error("lineage");
        long total=0;
        foreach(var digest in digests)
        {
            ct.ThrowIfCancellationRequested();
            if(!Digest(digest)||!revision.Inputs.TryGetValue(digest,out var bytes)||bytes.IsDefault||
                bytes.Length>64L*1024*1024-total||Hash(bytes.AsSpan())!=digest)
                throw Error("lineage");
            total+=bytes.Length;
        }
        return digests;
    }
    private static ReadOnlyMemory<byte>? FindFacts(StoredRevision r,EvidenceBinding b)=>
        b.FactsSha256 is null?null:new ReadOnlyMemory<byte>(r.Inputs[b.FactsSha256].ToArray());

    private static void ValidateInvocation(RunInvocation x)
    {
        if(!Id(x.AttemptId)||!Digest(x.AdapterPackageSha256)||!Id(x.CompilerVersion)||!Id(x.PlannerVersion)||
            !Id(x.ProfileId)||!Digest(x.VerifierBuildSha256)||!Id(x.RuntimeClass)||!Id(x.HardwareClass)||x.LogicalProcessors<=0||
            x.HostMemoryBytes<=0||x.Threads<=0||x.Threads>x.LogicalProcessors||x.MemoryBudgetBytes<=0||
            x.MemoryBudgetBytes>x.HostMemoryBytes||x.TimeoutMs<=0||x.StartedAt.Offset!=TimeSpan.Zero||
            x.FinishedAt.Offset!=TimeSpan.Zero||x.StartedAt>x.FinishedAt||x.MaximumAssignments is <1 or >4096)
            throw Error("invocation");
    }
    // Settings are opaque replay data with bounded strict JSON; host owns approved adapter-specific
    // schema and must never include connector credentials or arbitrary executable commands.
    private static void ValidateSettings(byte[] bytes)
    {
        try
        {
            using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions{AllowDuplicateProperties=false,MaxDepth=16});
            if(json.RootElement.ValueKind!=JsonValueKind.Object)throw Error("settings");
        }
        catch(JsonException){throw Error("settings");}
    }
    private static void ValidateParent(string? parent){if(parent is not null&&!Digest(parent))throw Error("parent");}
    internal static bool Id(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>256||value!=value.Trim()||value.Any(char.IsControl))return false;
        try{_=new System.Text.UTF8Encoding(false,true).GetByteCount(value);return true;}
        catch(System.Text.EncoderFallbackException){return false;}
    }
    internal static bool Digest(string? value)=>value is {Length:64}&&value.All(char.IsAsciiHexDigitLower);
    internal static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static ContractException Error(string code)=>new("evidence."+code,"Evidence validation failed: "+code+".");
    private static byte[] Copy(ReadOnlyMemory<byte> bytes,int limit)
    {if(bytes.Length<1||bytes.Length>limit)throw Error("size");return bytes.ToArray();}
    private static T Decode<T>(byte[] bytes) where T:class
    {
        try{return JsonSerializer.Deserialize<T>(bytes,Options)??throw Error("payload");}
        catch(JsonException){throw Error("payload");}
    }
    // Stable ordinal object ordering, preserve array ordering, no floating point transformations.
    // Internal versioned byte convention, not a claim of generic RFC 8785 canonicalization.
    private static byte[] Encode<T>(T value)
    {
        var node=JsonSerializer.SerializeToElement(value,Options);
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))WriteCanonical(writer,node);
        if(stream.Length>MaximumArtifactBytes)throw Error("size");
        return stream.ToArray();
    }
    private static void WriteCanonical(Utf8JsonWriter writer,JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach(var p in value.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal))
            {writer.WritePropertyName(p.Name);WriteCanonical(writer,p.Value);}
            writer.WriteEndObject();
        }
        else if(value.ValueKind==JsonValueKind.Array)
        {
            writer.WriteStartArray();foreach(var item in value.EnumerateArray())WriteCanonical(writer,item);writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
