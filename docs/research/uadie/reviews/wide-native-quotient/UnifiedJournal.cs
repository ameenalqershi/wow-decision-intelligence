using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
internal record JournalHead(int Sequence,string Hash);
internal record JournalBody(string Tenant,int Sequence,string Previous,UnifiedEvidence Evidence);
internal record JournalRecord(JournalBody Body,string Hash);
internal static class UnifiedJournal
{
    private static readonly JsonSerializerOptions Json=CertifiedQuotient.Json;
    private const int MaxRecord=8*1024*1024;
    internal static readonly JournalHead Empty=new(-1,"GENESIS");
    private static string Hash(JournalBody b)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(b,Json)));
    private static void Tenant(string t){if(t.Length is <1 or >64||t.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))throw new ArgumentException("tenant label");}
    internal static JournalHead Read(string dir,string tenant,JournalHead? expected=null)
    {
        Tenant(tenant);var head=Empty;bool seen=expected==null||expected==Empty;
        var paths=Directory.GetFiles(dir,"*.json").Order(StringComparer.Ordinal).ToArray();
        if(paths.Length>10000)throw new Exception("record limit");
        foreach(string p in paths){
            if(new FileInfo(p).Length>MaxRecord)throw new Exception("record length");
            var r=JsonSerializer.Deserialize<JournalRecord>(File.ReadAllBytes(p),Json)??throw new Exception("record");
            var b=r.Body;
            if(b.Tenant!=tenant||b.Sequence!=head.Sequence+1||Path.GetFileName(p)!=$"{b.Sequence:D8}.json"||b.Previous!=head.Hash||r.Hash!=Hash(b))throw new Exception("chain binding");
            UnifiedVerifier.Verify(b.Evidence);head=new(b.Sequence,r.Hash);if(expected==head)seen=true;
        }
        if(!seen)throw new Exception("missing trusted receipt");
        return head;
    }
    private static void Crash(string actual,string stage){if(actual==stage){Process.GetCurrentProcess().Kill();Environment.Exit(99);}}
    internal static JournalHead Append(string dir,string tenant,JournalHead expected,UnifiedEvidence evidence,string stage="none")
    {
        Tenant(tenant);
        using var lease=new FileStream(Path.Combine(dir,"writer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var head=Read(dir,tenant,expected);if(head!=expected)throw new Exception("stale head");
        UnifiedVerifier.Verify(evidence);
        var body=new JournalBody(tenant,checked(head.Sequence+1),head.Hash,evidence);var record=new JournalRecord(body,Hash(body));
        byte[] data=JsonSerializer.SerializeToUtf8Bytes(record,Json);if(data.Length>MaxRecord)throw new Exception("record length");
        string tmp=Path.Combine(dir,Guid.NewGuid().ToString("N")+".tmp");
        using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
            int middle=data.Length/2;f.Write(data.AsSpan(0,middle));f.Flush(true);Crash("partial",stage);
            f.Write(data.AsSpan(middle));f.Flush(true);Crash("flushed",stage);
        }
        File.Move(tmp,Path.Combine(dir,$"{body.Sequence:D8}.json"),false);Crash("renamed",stage);
        SyncDirectory(dir);Crash("synced",stage);
        return new(body.Sequence,record.Hash);
    }
    private static void SyncDirectory(string dir)
    {
        if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("Linux directory sync only");
        int fd=Native.open(dir,0x10000);if(fd<0)throw new IOException("open directory");
        try{if(Native.fsync(fd)!=0)throw new IOException("directory sync");}finally{_=Native.close(fd);}
    }
    private static class Native{
        [DllImport("libc",SetLastError=true)]internal static extern int open(string path,int flags);
        [DllImport("libc",SetLastError=true)]internal static extern int fsync(int fd);
        [DllImport("libc",SetLastError=true)]internal static extern int close(int fd);
    }
    private static UnifiedEvidence[] Source(string file)=>File.ReadLines(file).Select(l=>JsonSerializer.Deserialize<UnifiedEvidence>(l,Json)!).Where(e=>e.Outcome.Formal).Take(3).ToArray();
    internal static void Child(string dir,string source,string stage,string seq,string hash)
    {
        var e=Source(source)[1];
        _=Append(dir,"tenant-a",new(int.Parse(seq),hash),e,stage);
    }
    internal static void Audit(string source,string root)
    {
        var e=Source(source);if(e.Length!=3)throw new Exception("source");
        int checks=0;
        void Assert(bool b,string msg){if(!b)throw new Exception(msg);}
        Process ChildProcess(string dir,string stage,JournalHead head)
        {
            var psi=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true};
            foreach(string arg in new[]{typeof(UnifiedJournal).Assembly.Location,"unified-journal-child",dir,source,stage,head.Sequence.ToString(),head.Hash})psi.ArgumentList.Add(arg);
            return Process.Start(psi)??throw new Exception("child");
        }
        foreach(string stage in new[]{"partial","flushed","renamed","synced"}){
            string dir=Path.Combine(root,stage);Directory.CreateDirectory(dir);
            var first=Append(dir,"tenant-a",Empty,e[0]);using var child=ChildProcess(dir,stage,first);
            if(!child.WaitForExit(15000)){child.Kill();throw new Exception("child timeout");}
            Assert(child.ExitCode!=0,"child not killed");
            var head=Read(dir,"tenant-a",first);
            Assert(head.Sequence==(stage is "partial" or "flushed"?0:1),"commit visibility");
            var resumed=Append(dir,"tenant-a",head,e[2]);Assert(Read(dir,"tenant-a",resumed)==resumed,"recovery");
            Console.WriteLine(JsonSerializer.Serialize(new{kind="crash",stage,exit=child.ExitCode,visible=head.Sequence+1,recovered=resumed.Sequence+1,temps=Directory.GetFiles(dir,"*.tmp").Length,valid=true}));checks++;
        }
        string race=Path.Combine(root,"race");Directory.CreateDirectory(race);
        var baseline=Append(race,"tenant-a",Empty,e[0]);
        using(var a=ChildProcess(race,"none",baseline))using(var b=ChildProcess(race,"none",baseline)){
            if(!a.WaitForExit(15000)||!b.WaitForExit(15000))throw new Exception("race timeout");
            Assert((a.ExitCode==0)!=(b.ExitCode==0),"single writer");
            Assert(Read(race,"tenant-a",baseline).Sequence==1,"race chain");
            Console.WriteLine(JsonSerializer.Serialize(new{kind="race",exits=new[]{a.ExitCode,b.ExitCode},valid=true}));checks++;
        }
        void Reject(string name,Action run){bool ok=false;try{run();}catch{ok=true;}Assert(ok,name);Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",name,rejected=ok}));checks++;}
        var latest=Read(race,"tenant-a");
        Reject("cross-tenant",()=>Read(race,"tenant-b"));
        Reject("stale-revision",()=>Append(race,"tenant-a",baseline,e[2]));
        string last=Path.Combine(race,"00000001.json");byte[] original=File.ReadAllBytes(last);
        File.Delete(last);Reject("suffix-deletion-with-receipt",()=>Read(race,"tenant-a",latest));File.WriteAllBytes(last,original);
        File.WriteAllBytes(last,original.AsSpan(0,original.Length/2).ToArray());Reject("truncated-commit",()=>Read(race,"tenant-a",latest));File.WriteAllBytes(last,original);
        var r=JsonSerializer.Deserialize<JournalRecord>(original,Json)!;
        File.WriteAllBytes(last,JsonSerializer.SerializeToUtf8Bytes(r with{Hash="bad"},Json));Reject("forged-hash",()=>Read(race,"tenant-a",latest));File.WriteAllBytes(last,original);
        string firstFile=Path.Combine(race,"00000000.json");byte[] firstBytes=File.ReadAllBytes(firstFile);File.Delete(firstFile);Reject("missing-prefix",()=>Read(race,"tenant-a",latest));File.WriteAllBytes(firstFile,firstBytes);
        Assert(Read(race,"tenant-a",latest)==latest,"final recovery");
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",checks,valid=true}));
    }
}
