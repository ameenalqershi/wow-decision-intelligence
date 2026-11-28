using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
internal record StructuralBlob(WideModel Shape,TransformCertificate? Proof);
internal record RevisionBlob(string Key,string Digest,int N,string Family,int Seed,int Revision,string Arm,long[] Cost,long? Offset,long[]? ReducedCost,ProofOutcome Outcome);
internal record EvidenceEnvelope(string Kind,string? Key,StructuralBlob? Structure,RevisionBlob? Revision);
internal static class ProofEvidenceStore
{
    private static readonly JsonSerializerOptions Json=CertifiedQuotient.Json;
    private static byte[] Bytes<T>(T t)=>JsonSerializer.SerializeToUtf8Bytes(t,Json);
    private static string Key(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
    private static void Append<T>(FileStream f,T record){var b=Bytes(record);f.Write(b);f.WriteByte(10);f.Flush(true);}
    private static (int records,int objects) WriteCompact(string file,ProofEvidence[] records)
    {
        var known=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        using var stream=new FileStream(file,FileMode.CreateNew);
        foreach(var e in records)
        {
            var shape=e.Model with{Cost=new long[e.N]};
            var cert=e.Outcome.Cert;
            var structural=cert==null?null:cert with{Digest=CertifiedQuotient.Digest(shape),Offset=0,Cost=new long[cert.Free]};
            var blob=new StructuralBlob(shape,structural);var bytes=Bytes(blob);string key=Key(bytes);
            if(known.TryGetValue(key,out var old)){if(!old.AsSpan().SequenceEqual(bytes))throw new Exception("hash collision");}
            else{Append(stream,new EvidenceEnvelope("object",key,blob,null));known.Add(key,bytes);}
            var rev=new RevisionBlob(key,e.Digest,e.N,e.Family,e.Seed,e.Revision,e.Arm,e.Model.Cost,cert?.Offset,cert?.Cost,e.Outcome with{Cert=null});
            Append(stream,new EvidenceEnvelope("revision",null,null,rev));
        }
        return(records.Length,known.Count);
    }
    private static ProofEvidence[] ReadCompact(IEnumerable<string> lines)
    {
        var objects=new Dictionary<string,StructuralBlob>(StringComparer.Ordinal);var result=new List<ProofEvidence>();
        foreach(string line in lines)
        {
            var e=JsonSerializer.Deserialize<EvidenceEnvelope>(line,Json)??throw new Exception("record");
            if(e.Kind=="object"){
                var b=e.Structure??throw new Exception("missing object");
                if(e.Key==null||Key(Bytes(b))!=e.Key||e.Revision!=null||b.Shape.Cost.Any(c=>c!=0))throw new Exception("object binding");
                if(!objects.TryAdd(e.Key,b))throw new Exception("duplicate object");
                IncrementalQuotient.Admit(b.Shape);
                if(b.Proof!=null&&!TransformationVerifier.Verify(b.Shape,CertifiedQuotient.Digest(b.Shape),b.Proof))throw new Exception("stored structural proof");
            }else if(e.Kind=="revision"){
                var r=e.Revision??throw new Exception("missing revision");
                if(e.Key!=null||e.Structure!=null||!objects.TryGetValue(r.Key,out var b)||r.Outcome.Cert!=null)throw new Exception("revision reference");
                var m=b.Shape with{Cost=r.Cost};TransformCertificate? c=null;
                if(b.Proof!=null)c=b.Proof with{Digest=r.Digest,Offset=r.Offset??throw new Exception("offset"),Cost=r.ReducedCost??throw new Exception("cost")};
                else if(r.Offset!=null||r.ReducedCost!=null)throw new Exception("unexpected objective certificate");
                var full=new ProofEvidence(r.Digest,r.N,r.Family,r.Seed,r.Revision,r.Arm,m,r.Outcome with{Cert=c});
                ProofReadback.Verify(full);result.Add(full);
            }else throw new Exception("unknown envelope");
        }
        return result.ToArray();
    }
    internal static void Run(string input,string dir)
    {
        var source=File.ReadLines(input).Select(line=>JsonSerializer.Deserialize<ProofEvidence>(line,Json)??throw new Exception("source")).ToArray();
        foreach(var r in source)ProofReadback.Verify(r);
        for(int rep=0;rep<3;rep++)
        foreach(string mode in rep%2==0?new[]{"full","compact"}:new[]{"compact","full"})
        {
            string file=Path.Combine(dir,mode+"-"+rep+".jsonl");
            long start=Stopwatch.GetTimestamp();int objects=0;
            if(mode=="full"){using var f=new FileStream(file,FileMode.CreateNew);foreach(var r in source)Append(f,r);}
            else{(_,objects)=WriteCompact(file,source);}
            long written=Stopwatch.GetTimestamp();
            var recovered=mode=="full"?File.ReadLines(file).Select(line=>JsonSerializer.Deserialize<ProofEvidence>(line,Json)??throw new Exception("record")).ToArray():ReadCompact(File.ReadLines(file));
            if(mode=="full")foreach(var r in recovered)ProofReadback.Verify(r);
            if(recovered.Length!=source.Length)throw new Exception("record count");
            for(int index=0;index<source.Length;index++)if(!Bytes(source[index]).AsSpan().SequenceEqual(Bytes(recovered[index])))throw new Exception("record replay mismatch");
            long end=Stopwatch.GetTimestamp();
            Console.WriteLine(JsonSerializer.Serialize(new{kind="storage",mode,rep,records=recovered.Length,objects,bytes=new FileInfo(file).Length,writeTicks=written-start,readbackTicks=end-written,frequency=Stopwatch.Frequency}));
        }
        string[] lines=File.ReadAllLines(Path.Combine(dir,"compact-0.jsonl"));
        void Reject(string name,IEnumerable<string> bad){bool rejected=false;try{_=ReadCompact(bad);}catch(Exception){rejected=true;}Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",name,rejected}));if(!rejected)throw new Exception(name);}
        Reject("missing-object",lines.Skip(1));
        var first=JsonSerializer.Deserialize<EvidenceEnvelope>(lines[0],Json)!;
        Reject("object-key",new[]{JsonSerializer.Serialize(first with{Key="forged"},Json)}.Concat(lines.Skip(1)));
        int i=Array.FindIndex(lines,l=>JsonSerializer.Deserialize<EvidenceEnvelope>(l,Json)!.Kind=="revision");
        var e=JsonSerializer.Deserialize<EvidenceEnvelope>(lines[i],Json)!;var rev=e.Revision!;
        long[] badCost=rev.Cost.ToArray();badCost[0]++;
        var copy=lines.ToArray();copy[i]=JsonSerializer.Serialize(e with{Revision=rev with{Cost=badCost}},Json);Reject("revision-objective",copy);
        copy=lines.ToArray();copy[i]=JsonSerializer.Serialize(e with{Revision=rev with{Key="unknown"}},Json);Reject("missing-reference",copy);
    }
}
