using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class TemplateStudy
{
    internal static void Run(string split,Func<WideModel,WideResult> original,Func<WideModel,WideResult> quotient,Func<WideModel,int[],long> verify)
    {
        bool scale=split=="scale";if(!scale&&split!="holdout")throw new ArgumentException("split");
        string[] arms=["original","G11","G13","template-only","G14"];
        var options=new JsonSerializerOptions{IncludeFields=true};
        foreach(int n in scale?new[]{50000,100000}:new[]{512,4096,25000})
        foreach(string family in scale?new[]{"stable","edits"}:new[]{"stable","edits","weak","none"})
        for(int h=0;h<(scale?1:2);h++)
        {
            int seed=(scale?2000101:1970101)+h;var rng=new Random(seed+n);
            var planted=Enumerable.Range(0,n).Select(_=>rng.Next(2)).ToArray();
            var edges=new List<(int A,int B)>();int covered=family=="none"?0:family=="weak"?n/4:n;
            int width=family=="weak"?2:9;
            for(int i=0;i<covered;i+=width)
                for(int j=i+1;j<Math.Min(i+width,covered);j++){edges.Add((i,j));planted[j]=1-planted[i];}
            var rows=new List<WideRow>();
            for(int i=0;i<n;i+=18){int[] scope=Enumerable.Range(i,Math.Min(18,n-i)).ToArray();int k=scope.Sum(j=>planted[j]);rows.Add(new(scope,k,k));}
            var fixes=new List<(int Bit,int Value)>();
            var g13=new IncrementalQuotient(true);var template=new IncrementalQuotient(true,true);var g14=new IncrementalQuotient(true,true,true);
            for(int rev=0;rev<(scale?3:4);rev++)
            {
                if(family=="edits"&&rev==2){edges.RemoveAt(0);fixes.Add((0,planted[0]));}
                if(family=="edits"&&rev==3){rows[0]=new(rows[0].Scope.Reverse().ToArray(),rows[0].L,rows[0].U);}
                var m=new WideModel(n,edges.ToArray(),fixes.ToArray(),rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-100,101)).ToArray());
                _=verify(m,planted);
                var results=new Dictionary<string,WideResult>();
                for(int a=0;a<arms.Length;a++)
                {
                    string arm=arms[(h+rev+a)%arms.Length];long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();
                    var trace=new PhaseTrace();
                    // All arms validate once. G13/G14 admission is internal.
                    if(arm is "original" or "G11")IncrementalQuotient.Admit(m);
                    trace.Mark("outer-admission");
                    string digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m,options)));trace.Mark("digest");
                    var session=arm switch{"G13"=>g13,"template-only"=>template,"G14"=>g14,_=>null};
                    var r=session!=null?session.Solve(m,original,trace):arm=="original"?original(m):quotient(m);
                    trace.Mark("solve-rest");
                    if(r.Status is "ModelInvalid" or "Invalid" or "Infeasible")throw new Exception("planted feasible rejected");
                    if(r.Bits!=null&&verify(m,r.Bits)!=r.Value)throw new Exception("source witness");
                    trace.Mark("verify");
                    var bytes=JsonSerializer.SerializeToUtf8Bytes(new{digest,n,family,seed,revision=rev,arm,r.Status,r.Value,r.Bits},options);trace.Mark("encode");
                    long ticks=Stopwatch.GetTimestamp()-start;
                    results.Add(arm,r);
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,revision=rev,arm,digest,status=r.Status,value=r.Value,verifiedWitness=r.Bits!=null,variables=r.Variables,rows=r.Rows,hit=session?.Hit??false,templateHit=session?.TemplateHit??false,route=session?.Route??arm,phases=trace.Ticks,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc},options));
                }
                foreach(var a in results.Values)foreach(var b in results.Values)
                {
                    if(a.Status=="Optimal"&&b.Status=="Optimal"&&a.Value!=b.Value)throw new Exception("optimal disagreement");
                    if(a.Status=="Optimal"&&b.Value<a.Value)throw new Exception("better than optimum");
                }
            }
        }
    }
}
