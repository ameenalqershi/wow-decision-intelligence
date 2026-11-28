using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WowDi.Contracts;
using WowDi.Dml.Binding;
using WowDi.Dml.Semantics;
using WowDi.Udir;
using Xunit;

namespace WowDi.Optimization.Fingerprints.Tests;
public sealed class FingerprintTests
{
    private static readonly byte[] Source=Encoding.UTF8.GetBytes("problem P { decision x : Int in [0,3]; decision y : Int in [0,3]; constraint a : x+y <= 4; constraint b : y >= 1; minimize goal priority 1 : 2*x+y; }");
    private static readonly byte[] Catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"1"}],"currencies":[],"calendars":["shift"]}""");
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static FingerprintContext Context()=>new("compiler1",Hash("compiler"u8.ToArray()),"planner1","verify1","solver","1","linux-x64",Hash("package"u8.ToArray()),"integer",Hash(Catalog),null,"{}"u8.ToArray(),"""{"threads":1}"""u8.ToArray());
    private static (UdirDocument Doc,Dictionary<string,ReadOnlyMemory<byte>> Inputs,FingerprintContext Config) Fixture()
    {
        var b=DmlBinder.Bind("source",Source,new BindingContext());Assert.True(b.Accepted,b.Diagnostic?.Message);
        var c=DmlCompiler.Compile(b.Document!,new("tenant","model","r1",new("shift","UTC","2026-01-01T00:00:00Z","minute"),new([new("minute","time","1")],[],["shift"]),[Hash(Catalog)],[],["integer.linear"]));
        Assert.True(c.Accepted,c.Diagnostic?.Message);
        var compilation=c.Compilation!;
        var inputs=new Dictionary<string,ReadOnlyMemory<byte>>(StringComparer.Ordinal){{Hash(Source),Source},{Hash(Catalog),Catalog}};
        foreach(var f in compilation.GeneratedInputs)inputs[f.Key]=f.Value.ToArray();
        var d=compilation.Original;
        return (d,inputs,Context() with{FactsSha256=compilation.FactsDigest});
    }
    [Fact]
    public void StableSetReorderingButNotProvenance()
    {
        var (d,i,c)=Fixture();
        var a=ModelFingerprints.Create(d,i,c);
        var m=d.Model;
        var reorder=UdirDocument.Create(m with{InputDigests=m.InputDigests.Reverse().ToImmutableArray(),Types=m.Types.Reverse().ToImmutableArray(),
            Symbols=m.Symbols.Reverse().ToImmutableArray(),Expressions=m.Expressions.Reverse().ToImmutableArray(),Constraints=m.Constraints.Reverse().ToImmutableArray()});
        var b=ModelFingerprints.Create(reorder,i.Reverse().ToDictionary(p=>p.Key,p=>p.Value),c);
        Assert.True(a.Structure.SameContent(b.Structure));Assert.True(a.Data.SameContent(b.Data));
        Assert.True(a.Constraints.SameContent(b.Constraints));Assert.True(a.Objectives.SameContent(b.Objectives));
        Assert.False(a.Provenance.SameContent(b.Provenance));
        var permission=b.Compare(a);
        Assert.True(permission.SameCanonicalInputsAndConfiguration);Assert.True(permission.HintRequiresOriginalVerification);
        Assert.False(permission.PlanRequiresHostRebind);Assert.False(permission.CanTransferBound);Assert.False(permission.CanTransferEvidence);
        Assert.True(a.Compare(ModelFingerprints.Create(d,i,c)).PlanRequiresHostRebind);
        Assert.NotEqual(a.Structure.Digest,a.Constraints.Digest);
    }
    [Fact]
    public void DeletionAndObjectiveEditInvalidateOnlyAppropriateSlicesAndFullComparison()
    {
        var (d,i,c)=Fixture();var a=ModelFingerprints.Create(d,i,c);
        var noRow=ModelFingerprints.Create(UdirDocument.Create(d.Model with{Constraints=d.Model.Constraints.RemoveAt(0)}),i,c);
        Assert.False(noRow.Constraints.SameContent(a.Constraints));Assert.True(noRow.Objectives.SameContent(a.Objectives));
        Assert.False(noRow.Compare(a).SameCanonicalInputsAndConfiguration);Assert.True(noRow.Compare(a).HintRequiresOriginalVerification);
        var goal=d.Model.Objectives[0];
        var changed=ModelFingerprints.Create(UdirDocument.Create(d.Model with{Objectives=[goal with{Direction=Direction.Maximize}]}),i,c);
        Assert.True(changed.Constraints.SameContent(a.Constraints));Assert.False(changed.Objectives.SameContent(a.Objectives));
        Assert.False(changed.Compare(a).CanTransferBound);
        var literal=d.Model.Expressions.First(e=>e.Literal=="4");
        var edit=UdirDocument.Create(d.Model with{Expressions=d.Model.Expressions.Select(e=>e.Id==literal.Id?e with{Literal="3"}:e).ToImmutableArray()});
        var updated=ModelFingerprints.Create(edit,i,c);
        Assert.True(updated.Structure.SameContent(a.Structure));Assert.False(updated.Data.SameContent(a.Data));Assert.False(updated.Constraints.SameContent(a.Constraints));
    }
    [Fact]
    public void TenantModelRevisionCompilerAndAdapterAreDistinct()
    {
        var (d,i,c)=Fixture();var a=ModelFingerprints.Create(d,i,c);
        foreach(var changed in new[]{d.Model with{TenantId="other"},d.Model with{ModelId="other"}})
        {
            var b=ModelFingerprints.Create(UdirDocument.Create(changed),i,c);
            Assert.False(b.Structure.SameContent(a.Structure));Assert.Equal("foreignScope",b.Compare(a).Reason);
            Assert.False(b.Compare(a).HintRequiresOriginalVerification);
        }
        var rev=ModelFingerprints.Create(UdirDocument.Create(d.Model with{RevisionId="r2"}),i,c);
        Assert.True(rev.Compare(a).SameCanonicalInputsAndConfiguration);Assert.False(rev.Compare(a).PlanRequiresHostRebind);
        foreach(var config in new[]{c with{CompilerVersion="2"},c with{AdapterVersion="2"},c with{RuntimeId="win-x64"},c with{Settings="""{"threads":2}"""u8.ToArray()}})
        {
            var b=ModelFingerprints.Create(d,i,config);
            Assert.False(b.SolverConfiguration.SameContent(a.SolverConfiguration));Assert.False(b.Compare(a).SameCanonicalInputsAndConfiguration);Assert.False(b.Compare(a).PlanRequiresHostRebind);
        }
    }
    [Fact]
    public void ActualDataAndDomainChangesCannotBeHidden()
    {
        var (d,i,c)=Fixture();var a=ModelFingerprints.Create(d,i,c);
        var updated=Encoding.UTF8.GetBytes("new input");i.Add(Hash(updated),updated);
        var b=ModelFingerprints.Create(UdirDocument.Create(d.Model with{InputDigests=d.Model.InputDigests.Add(Hash(updated))}),i,c);
        Assert.False(b.Data.SameContent(a.Data));Assert.False(b.Compare(a).SameCanonicalInputsAndConfiguration);
        i.Remove(Hash(updated));
        var domain=UdirDocument.Create(d.Model with{Symbols=d.Model.Symbols.Select(s=>s.Id=="x"?s with{Upper="2"}:s).ToImmutableArray()});
        var q=ModelFingerprints.Create(domain,i,c);Assert.False(q.Compare(a).HintRequiresOriginalVerification);
        i[Hash(Catalog)]="forged"u8.ToArray();
        Assert.Equal("fingerprint.inputDigest",Assert.Throws<ContractException>(()=>ModelFingerprints.Create(d,i,c)).Code);
    }
    [Fact]
    public void MissingSnapshotsAndUnknownFeaturesFailClosed()
    {
        var (d,i,c)=Fixture();var a=ModelFingerprints.Create(d,i,c);
        Assert.Equal("fingerprint.snapshot",Assert.Throws<ContractException>(()=>ModelFingerprints.Create(d,i,c with{CatalogSha256=new string('0',64)})).Code);
        var unknown=ModelFingerprints.Create(UdirDocument.Create(d.Model with{Requires=["future.feature"]}),i,c);
        Assert.Equal("unsupportedProfile",unknown.Compare(a).Reason);
        Assert.False(unknown.Compare(a).HintRequiresOriginalVerification);
        i.Remove(Hash(Source));
        Assert.Throws<ContractException>(()=>ModelFingerprints.Create(d,i,c));
    }
    [Fact]
    public void InputAndConfigurationArraysAreSnapshotted()
    {
        var (d,i,c)=Fixture();
        var s=Encoding.UTF8.GetBytes("""{"threads":1}""");c=c with{Settings=s};
        var a=ModelFingerprints.Create(d,i,c);
        var b=ModelFingerprints.Create(d,i,c);
        s[0]=0;
        Assert.True(a.SolverConfiguration.SameContent(b.SolverConfiguration));
        Assert.False(a.SolverConfiguration.SameContent(ModelFingerprints.Create(d,i,c).SolverConfiguration));
    }

    [Fact]
    public void IllFormedIdentityAndUnitsCannotAlias()
    {
        var (d,i,c)=Fixture();var a=ModelFingerprints.Create(d,i,c);
        var typeId=d.Model.Symbols.First(s=>s.Id=="x").TypeId;
        var changed=UdirDocument.Create(d.Model with{Types=d.Model.Types.Select(t=>t.Id==typeId?t with{Unit="minute"}:t).ToImmutableArray()});
        var q=ModelFingerprints.Create(changed,i,c);
        Assert.False(q.Structure.SameContent(a.Structure));Assert.False(q.Compare(a).HintRequiresOriginalVerification);
        var bad=UdirDocument.Create(d.Model with{Requires=["bad"+(char)0xD800]});
        Assert.Equal("fingerprint.unicode",Assert.Throws<ContractException>(()=>ModelFingerprints.Create(bad,i,c)).Code);
    }
}
