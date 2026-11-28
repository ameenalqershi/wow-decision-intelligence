using System.Diagnostics;
using System.Text.Json;
internal static class MatrixRepositoryAudit
{
    private static MatrixEvidence[] Source(string file)=>File.ReadLines(file).Select(l=>JsonSerializer.Deserialize<MatrixEvidence>(l,CertifiedQuotient.Json)??throw new Exception("source")).ToArray();
    internal static void Child(string dir,string source,string stage){
        var r=MatrixEvidenceRepository.Append(dir,"tenant-a",UnifiedJournal.Empty,Source(source).First(e=>e.Assurance.StartsWith("Proven")),stage);
        Console.WriteLine(JsonSerializer.Serialize(r));Environment.Exit(r.Status=="Committed"?0:20);
    }
    internal static void Run(string file,string root){
        var source=Source(file);int checks=0;
        void Pass(string name,bool ok){if(!ok)throw new Exception(name);Console.WriteLine(JsonSerializer.Serialize(new{kind="check",name,valid=true}));checks++;}
        void Reject(string name,Action action){bool rejected=false;try{action();}catch{rejected=true;}Pass(name,rejected);}
        Process Launch(string dir,string stage){
            var p=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string x in new[]{typeof(MatrixRepositoryAudit).Assembly.Location,"matrix-repo-child",dir,file,stage})p.ArgumentList.Add(x);
            return Process.Start(p)??throw new Exception("child");
        }
        string main=Path.Combine(root,"main");Directory.CreateDirectory(main);
        long start=Stopwatch.GetTimestamp();var head=UnifiedJournal.Empty;
        foreach(var e in source){var r=MatrixEvidenceRepository.Append(main,"tenant-a",head,e);if(r.Status!="Committed")throw new Exception("append");head=r.Head;}
        long write=Stopwatch.GetTimestamp();var replay=MatrixEvidenceRepository.Read(main,"tenant-a",head);long end=Stopwatch.GetTimestamp();
        Pass("exact-replay",source.Length==replay.Records.Length&&source.Zip(replay.Records).All(p=>MatrixEvidenceRepository.Bytes(p.First).AsSpan().SequenceEqual(MatrixEvidenceRepository.Bytes(p.Second))));
        string[] objects=Directory.GetFiles(Path.Combine(main,"objects"),"*.obj");
        var revisions=Directory.GetFiles(main,"*.json");
        Console.WriteLine(JsonSerializer.Serialize(new{kind="storage",records=source.Length,objects=objects.Length,sourceBytes=new FileInfo(file).Length,objectBytes=objects.Sum(p=>new FileInfo(p).Length),revisionBytes=revisions.Sum(p=>new FileInfo(p).Length),writeMs=(write-start)*1000d/Stopwatch.Frequency,replayMs=(end-write)*1000d/Stopwatch.Frequency}));
        foreach(string stage in new[]{"before-object","object-partial","object-synced","before-revision","revision-partial","revision-synced"}){
            string dir=Path.Combine(root,stage);Directory.CreateDirectory(dir);
            using var child=Launch(dir,stage);if(!child.WaitForExit(15000)){child.Kill();throw new Exception("child timeout");}
            var r=MatrixEvidenceRepository.Read(dir,"tenant-a",UnifiedJournal.Empty);
            Pass(stage+"-visibility",child.ExitCode==137&&r.Head.Sequence==(stage=="revision-synced"?0:-1));
            var resumed=MatrixEvidenceRepository.Append(dir,"tenant-a",r.Head,source[0]);
            Pass(stage+"-recovery",resumed.Status=="Committed"&&MatrixEvidenceRepository.Read(dir,"tenant-a",resumed.Head).Head==resumed.Head);
        }
        string race=Path.Combine(root,"race");Directory.CreateDirectory(race);
        using(var a=Launch(race,"none"))using(var b=Launch(race,"none")){
            if(!a.WaitForExit(15000)||!b.WaitForExit(15000))throw new Exception("race timeout");
            Pass("graceful-race",new[]{a.ExitCode,b.ExitCode}.Order().SequenceEqual(new[]{0,20})&&MatrixEvidenceRepository.Read(race,"tenant-a",UnifiedJournal.Empty).Head.Sequence==0);
        }
        Reject("cross-tenant-label",()=>MatrixEvidenceRepository.Read(main,"tenant-b",head));
        Pass("stale-head",MatrixEvidenceRepository.Append(main,"tenant-a",UnifiedJournal.Empty,source[0]).Status=="Conflict");
        Reject("wrong-trusted-receipt",()=>MatrixEvidenceRepository.Read(main,"tenant-a",new(head.Sequence,"wrong")));
        string last=Path.Combine(main,$"{head.Sequence:D8}.json");byte[] original=File.ReadAllBytes(last);
        // Move files into the isolated test directory, then restore them; never touch external evidence.
        string moved=last+".saved";File.Move(last,moved);
        Reject("missing-published-revision",()=>MatrixEvidenceRepository.Read(main,"tenant-a",head));File.Move(moved,last);
        File.WriteAllBytes(last,original.AsSpan(0,original.Length/2).ToArray());
        Reject("truncated-revision",()=>MatrixEvidenceRepository.Read(main,"tenant-a",head));File.WriteAllBytes(last,original);
        var obj=objects[0];byte[] ob=File.ReadAllBytes(obj);string saved=obj+".saved";File.Move(obj,saved);
        Reject("missing-referenced-object",()=>MatrixEvidenceRepository.Read(main,"tenant-a",head));File.Move(saved,obj);
        byte[] bad=ob.ToArray();bad[bad.Length/2]^=1;File.WriteAllBytes(obj,bad);
        Reject("corrupt-object",()=>MatrixEvidenceRepository.Read(main,"tenant-a",head));File.WriteAllBytes(obj,ob);
        var forged=source[0] with{Assurance="ProvenOptimal",X=null};
        Reject("invalid-proof-publication",()=>MatrixEvidenceRepository.Append(main,"tenant-a",head,forged));
        // Failure of object directory creation, with no committed revision.
        string fail=Path.Combine(root,"failure");Directory.CreateDirectory(fail);File.WriteAllText(Path.Combine(fail,"objects"),"not a directory");
        Reject("storage-failure",()=>MatrixEvidenceRepository.Append(fail,"tenant-a",UnifiedJournal.Empty,source[0]));
        Pass("failed-publication-invisible",MatrixEvidenceRepository.Read(fail,"tenant-a",UnifiedJournal.Empty).Head==UnifiedJournal.Empty);
        Pass("restored",MatrixEvidenceRepository.Read(main,"tenant-a",head).Head==head);
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",checks,valid=true}));
    }
}
