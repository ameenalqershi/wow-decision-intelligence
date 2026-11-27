using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WowDi.Dml.Binding;
using WowDi.Dml.Semantics;
using WowDi.ModelImport;
using WowDi.ModelingSdk;
using WowDi.Samples;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Independent;
using Xunit;

namespace WowDi.Tests;

public sealed class FrontendEquivalenceTests
{
    private static byte[] Input(string path)=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,path));
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Theory]
    [InlineData("production")]
    [InlineData("workforce")]
    public void RealCSharpDmlAndIndependentJsonInputsHaveSameAdmittedMathematics(string name)
    {
        var catalog=ExampleModels.Catalog();
        var source=new ModelSource(name,Input(name+".txt"));
        var sdk=name=="production"?ExampleModels.Production(source):ExampleModels.Workforce(source);
        var fromSdk=sdk.Normalize(catalog);
        Assert.True(fromSdk.Accepted,fromSdk.Diagnostic?.Message);

        var bound=DmlBinder.Bind(name+".dml",Input(name+".dml"),new BindingContext());
        Assert.True(bound.Accepted,bound.Diagnostic?.Message);
        var compiled=DmlCompiler.Compile(bound.Document!,new("example",name,"dml-r1",ExampleModels.Basis(),catalog.Semantics,
            [catalog.Input.Digest],[new("USD",ScalarKind.Money,"USD"),new("hour",ScalarKind.Duration),
                new("hour/employee",ScalarKind.Rate),new("USD/hour",ScalarKind.Rate)],["integer.linear"]));
        Assert.True(compiled.Accepted,compiled.Diagnostic?.Code+": "+compiled.Diagnostic?.Message);
        var dml=compiled.Compilation!;

        // Authored independently of either builder: actual JSON input, not a reference output.
        var jsonBytes=Input("Json/"+name+".json");
        Assert.Null(VersionedModelImport.ValidateSchema(jsonBytes));
        var json=ModelBuilder.Import(jsonBytes,catalog.Semantics);
        var fromJson=ExactNormalizer.Normalize(json,catalog.Semantics);
        Assert.True(fromJson.Accepted,fromJson.Diagnostic?.Message);

        var expected=fromSdk.Model!.CanonicalBytes.ToArray();
        Assert.Equal(expected,dml.Normalized.CanonicalBytes.ToArray());
        Assert.Equal(expected,fromJson.Model!.CanonicalBytes.ToArray());
        Assert.Equal(fromSdk.Model.SemanticDigest,dml.Normalized.SemanticDigest);
        Assert.Equal(fromSdk.Model.SemanticDigest,fromJson.Model.SemanticDigest);
        // Different source and revision identities remain different and are never replaced by a semantic digest.
        Assert.NotEqual(sdk.Document.Write(),dml.Original.Write());
        Assert.NotEqual(sdk.Document.Write(),json.Write());
        Assert.NotEqual(dml.Original.Write(),json.Write());
        Assert.Equal(3,new[]{sdk.Document.Model.RevisionId,dml.Original.Model.RevisionId,json.Model.RevisionId}.Distinct().Count());

        var facts=Input("Json/"+name+".facts.json");
        Assert.Equal(Hash(facts),sdk.FactsDigest);
        Assert.Equal(Hash(facts),dml.FactsDigest);
        Assert.Equal(facts,sdk.Inputs[sdk.FactsDigest!].ToArray());
        Assert.Equal(facts,dml.GeneratedInputs[dml.FactsDigest].ToArray());
        Assert.Equal(Input("Json/catalog.json"),catalog.Input.Bytes.ToArray());
        Assert.Contains(Hash(facts),json.Model.InputDigests);
        Assert.Contains(catalog.Input.Digest,json.Model.InputDigests);
        Assert.Contains(source.Input.Digest,json.Model.InputDigests);
        Assert.Equal(source.Input.Digest,Assert.Single(json.Model.Sources).Sha256);
        Assert.Equal(source.Document.Utf16Length,Assert.Single(json.Model.Sources).Utf16Length);
        Assert.Equal(Hash(Input(name+".dml")),Assert.Single(dml.Original.Model.Sources).Sha256);
        Assert.Equal(dml.Original.Model.Expressions.Length,dml.Normalized.Origins.Length);

        if(name=="workforce")
        {
            // Independent enumerated test witnesses, not native optimizer results.
            foreach(var original in new[]{sdk.Document,dml.Original,json})
            for(int staff=0;staff<=12;staff++)
            {
                var result=new SolverResult(original.Model.TenantId,original.Model.ModelId,original.Model.RevisionId,
                    "enum",new("test-enumeration","1","test"),MathematicalStatus.Feasible,Termination.Completed,
                    ImmutableDictionary<string,string>.Empty.Add("staff",staff.ToString(CultureInfo.InvariantCulture)),
                    [new("cost",(staff*120).ToString(CultureInfo.InvariantCulture),null,"0","0")],null,new(null,null,null));
                var scope=new VerificationScope(original.Model.TenantId,original.Model.ModelId,original.Model.RevisionId,"enum",Hash(original.Write()));
                var verified=OriginalVerifier.Check(original,catalog.Semantics,scope,result,facts);
                Assert.Equal(staff>=8,verified.Accepted);
                Assert.Equal((staff*120).ToString(CultureInfo.InvariantCulture),Assert.Single(verified.Objectives).ExactValue);
            }
        }
        else
        {
            foreach(var original in new[]{sdk.Document,dml.Original,json})
            {
                Assert.Equal("10",original.Model.Scenarios.Single(s=>s.ParentId is null).Overrides[0].Value);
                Assert.Equal("4",original.Model.Scenarios.Single(s=>s.Id=="shortage").Overrides[0].Value);
            }
        }
    }
}
