using System.Collections.Immutable;
using System.Security.Cryptography;
using WowDi.Contracts;
using WowDi.ModelRegistry;
using WowDi.Optimization.Fingerprints;
using WowDi.Verification.Bounds;
using WowDi.Verification.Evidence;

namespace WowDi.Optimization.Cache;

/// <summary>All fields come from the authorized durable attempt/evidence ledger, never a caller-selected cached entry.</summary>
public sealed record CacheIdentity(EvidenceBinding Binding,RunInvocation Invocation,string EvidenceId,string PackageSha256);
public enum CacheDisposition { Stored, AlreadyStored, Hit, Miss, Rejected }
public sealed record CacheRead(CacheDisposition Disposition,EvidenceRecord? Record);

/// <summary>Bounded host-local replay cache. No cross-run bound transfer or publication authority.</summary>
public sealed class ExactEvidenceCache
{
    private sealed record Entry(CacheIdentity Identity,ModelFingerprints Fingerprints,ImmutableArray<byte> Package);
    private readonly SqliteModelRegistry registry;
    private readonly int capacity;
    private readonly object gate=new();
    private object invalidationEpoch=new();
    private readonly Dictionary<CacheIdentity,LinkedListNode<Entry>> entries=[];
    private readonly LinkedList<Entry> recent=[];

    public ExactEvidenceCache(SqliteModelRegistry registry,int capacity=4)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if(capacity is <1 or >8)throw new ArgumentOutOfRangeException(nameof(capacity));
        this.registry=registry;this.capacity=capacity;
    }
    public int Count{get{lock(gate)return entries.Count;}}
    private static bool Digest(string? s)=>s is {Length:64}&&s.All(char.IsAsciiHexDigitLower);
    private static string Hash(ReadOnlySpan<byte> b)=>Convert.ToHexStringLower(SHA256.HashData(b));
    private static bool Same(ReadOnlyMemory<byte> a,ImmutableArray<byte> b)=>a.Span.SequenceEqual(b.AsSpan());
    private static bool Valid(string trustedTenant,CacheIdentity id)=>id.Binding is {Scope:{ } s}&&
        !string.IsNullOrWhiteSpace(trustedTenant)&&trustedTenant==s.TenantId&&
        !string.IsNullOrWhiteSpace(s.ModelId)&&!string.IsNullOrWhiteSpace(s.RevisionId)&&!string.IsNullOrWhiteSpace(s.RunId)&&
        Digest(s.ModelSha256)&&Digest(id.EvidenceId)&&Digest(id.PackageSha256)&&id.Invocation is not null;
    private StoredRevision? Find(CacheIdentity id)
    {
        var s=id.Binding.Scope;
        return registry.Find(s.TenantId,s.ModelId,s.RevisionId);
    }
    private static ModelFingerprints Fingerprints(StoredRevision revision,FingerprintContext context,CancellationToken ct)=>
        ModelFingerprints.Create(revision.Document,revision.Inputs.ToDictionary(p=>p.Key,p=>(ReadOnlyMemory<byte>)p.Value.ToArray(),StringComparer.Ordinal),context,ct);

    private static bool Matches(CacheIdentity identity,EvidenceRecord record,FingerprintContext c)
    {
        var m=record.Manifest;var i=m.Invocation;var solver=record.Assessment.Solver;
        return !record.Assessment.Rejected&&identity.Binding==m.Binding&&identity.Invocation==i&&identity.EvidenceId==record.EvidenceId&&
            c.CatalogSha256==m.Binding.CatalogSha256&&c.FactsSha256==m.Binding.FactsSha256&&
            c.CompilerVersion==i.CompilerVersion&&c.PlannerVersion==i.PlannerVersion&&c.VerifierVersion==m.VerifierProfile&&
            c.NumericProfile==i.ProfileId&&c.AdapterId==solver.SolverId&&c.AdapterVersion==solver.Version&&
            c.RuntimeId==solver.RuntimeId&&c.AdapterPackageSha256==i.AdapterPackageSha256&&
            Same(c.Capabilities,record.CapabilitiesBytes)&&Same(c.Settings,record.SettingsBytes);
    }
    private static FingerprintContext Snapshot(FingerprintContext c)
    {
        // Avoid a mutable settings/capabilities buffer changing between admission and replay.
        if(c.Capabilities.Length is <1 or >262144||c.Settings.Length is <1 or >65536)
            throw new ContractException("cache.configSize","Invalid cache configuration size.");
        return c with{Capabilities=c.Capabilities.ToArray(),Settings=c.Settings.ToArray()};
    }

    public CacheDisposition Remember(string trustedTenant,CacheIdentity identity,FingerprintContext context,
        ReadOnlyMemory<byte> package,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(identity);ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if(!Valid(trustedTenant,identity)||package.Length is <1 or >EvidencePackage.MaximumPackageBytes)return CacheDisposition.Rejected;
        object epoch;lock(gate)epoch=invalidationEpoch;
        try
        {
            var config=Snapshot(context);
            var revision=Find(identity);
            if(revision is null)return CacheDisposition.Rejected;
            var bytes=package.ToArray();
            if(Hash(bytes)!=identity.PackageSha256)return CacheDisposition.Rejected;
            var record=EvidencePackage.Replay(revision,trustedTenant,identity.EvidenceId,identity.PackageSha256,bytes,ct);
            if(!Matches(identity,record,config))return CacheDisposition.Rejected;
            var fingerprint=Fingerprints(revision,config,ct);
            if(!fingerprint.Compare(fingerprint).PlanRequiresHostRebind)return CacheDisposition.Rejected;
            var entry=new Entry(identity,fingerprint,ImmutableArray.CreateRange(bytes));
            ct.ThrowIfCancellationRequested();
            lock(gate)
            {
                if(!ReferenceEquals(epoch,invalidationEpoch))return CacheDisposition.Rejected;
                if(entries.TryGetValue(identity,out var old))
                {
                    if(!fingerprint.Compare(old.Value.Fingerprints).PlanRequiresHostRebind||
                        !old.Value.Package.AsSpan().SequenceEqual(bytes))return CacheDisposition.Rejected;
                    recent.Remove(old);recent.AddFirst(old);return CacheDisposition.AlreadyStored;
                }
                while(entries.Count>=capacity)
                {
                    var tail=recent.Last!;entries.Remove(tail.Value.Identity);recent.RemoveLast();
                }
                entries.Add(identity,recent.AddFirst(entry));return CacheDisposition.Stored;
            }
        }
        catch(ContractException){return CacheDisposition.Rejected;}
    }

    public CacheRead Read(string trustedTenant,CacheIdentity identity,FingerprintContext context,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(identity);ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if(!Valid(trustedTenant,identity))return new(CacheDisposition.Miss,null);
        LinkedListNode<Entry>? node;
        lock(gate)if(!entries.TryGetValue(identity,out node))return new(CacheDisposition.Miss,null);
        try
        {
            var config=Snapshot(context);
            var revision=Find(identity);
            if(revision is null)return new(CacheDisposition.Miss,null);
            var current=Fingerprints(revision,config,ct);
            if(!current.Compare(node.Value.Fingerprints).PlanRequiresHostRebind)return new(CacheDisposition.Miss,null);
            var record=EvidencePackage.Replay(revision,trustedTenant,identity.EvidenceId,identity.PackageSha256,node.Value.Package.ToArray(),ct);
            if(!Matches(identity,record,config))return new(CacheDisposition.Miss,null);
            ct.ThrowIfCancellationRequested();
            lock(gate)
            {
                // If this entry was evicted during replay, fail closed instead of resurrecting it.
                if(!entries.TryGetValue(identity,out var live)||!ReferenceEquals(live,node))return new(CacheDisposition.Miss,null);
                recent.Remove(node);recent.AddFirst(node);
            }
            return new(CacheDisposition.Hit,record);
        }
        catch(ContractException){return new(CacheDisposition.Miss,null);}
    }

    /// <summary>Host calls after authorization/retention changes; this does not change the durable ledger.</summary>
    public int InvalidateTenant(string trustedTenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedTenant);
        lock(gate)
        {
            invalidationEpoch=new object();
            var keys=entries.Keys.Where(k=>k.Binding.Scope.TenantId==trustedTenant).ToArray();
            foreach(var k in keys){recent.Remove(entries[k]);entries.Remove(k);}
            return keys.Length;
        }
    }
}
