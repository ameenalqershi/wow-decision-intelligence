using System.Diagnostics;
using System.Text.Json;
internal static class GeneralFlowStudy
{
    internal static GeneralNetwork Model(int k,int seed,string family,int rev)
    {
        var rng=new Random(seed+rev*10007+k*101);var arcs=new List<GeneralArc>();var b=new long[k];
        for(int i=0;i<k;i++)for(int j=0;j<k;j++)if(i!=j&&(j==(i+1)%k||rng.Next(6)==0)){
            long l=family=="lower"?rng.Next(0,3):0,u=l+rng.Next(1,5),x=rng.Next((int)l,(int)u+1);
            arcs.Add(new(i,j,l,u,rng.Next(-20,21)));b[i]+=x;b[j]-=x;
        }
        if(family=="infeasible"){b[0]+=100000;b[1]-=100000;}
        return new(k,b,arcs.ToArray());
    }
    internal static void Run()
    {
        string[] arms=["CP-SAT","native-flow","G22"];
        foreach(int k in new[]{12,48})foreach(string family in new[]{"signed","lower","infeasible"})for(int h=0;h<12;h++)for(int rev=0;rev<2;rev++){
            int seed=3030101+h;var m=Model(k,seed,family,rev);var outcomes=new List<NetworkDecision>();
            for(int a=0;a<3;a++){
                string arm=arms[(h+rev+a)%3];long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();var trace=new PhaseTrace();
                GeneralFlow.Admit(m);string digest=GeneralFlow.Digest(m);trace.Mark("admit");
                var r=arm=="CP-SAT"?GeneralFlow.Original(m):GeneralFlow.Solve(m,arm=="G22");trace.Mark("solve");
                if(!NetworkVerifier.Verify(m,r,arm=="G22"))throw new Exception("verification");trace.Mark("verify");
                byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new{digest,model=m,result=r},CertifiedQuotient.Json);trace.Mark("encode");
                long ticks=Stopwatch.GetTimestamp()-start;outcomes.Add(r);
                Console.WriteLine(JsonSerializer.Serialize(new{n=k,family,seed,rev,arm,digest,status=r.Status,value=r.Value,formal=r.Potential!=null||r.Cut!=null,arcs=m.Arcs.Length,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=bytes.Length,phases=trace.Ticks}));
            }
            foreach(var x in outcomes)foreach(var y in outcomes)if(x.Status=="Optimal"&&y.Status=="Optimal"&&x.Value!=y.Value||x.Status=="Infeasible"&&y.Flow!=null)throw new Exception("disagreement");
        }
    }
    internal static void Audit()
    {
        int finite=0,tamper=0,infeasible=0;long vectors=0;
        for(int h=0;h<96;h++){
            var rng=new Random(3060101+h);var a=new[]{new GeneralArc(0,1,0,2,rng.Next(-8,9)),new GeneralArc(1,2,1,2,rng.Next(-8,9)),new GeneralArc(2,0,0,2,rng.Next(-8,9)),new GeneralArc(0,2,0,1,rng.Next(-8,9)),new GeneralArc(1,0,0,2,rng.Next(-8,9))};
            long b0=rng.Next(-3,4),b1=rng.Next(-3,4);var m=new GeneralNetwork(3,[b0,b1,-b0-b1],a);
            GeneralFlow.Admit(m);long? best=null;var x=new long[a.Length];
            void Visit(int i){
                if(i==x.Length){vectors++;long[] b=new long[3];long value=0;for(int j=0;j<a.Length;j++){b[a[j].From]+=x[j];b[a[j].To]-=x[j];value+=x[j]*a[j].Cost;}if(b.AsSpan().SequenceEqual(m.Supply)&&(best==null||value<best))best=value;return;}
                for(long v=a[i].Lower;v<=a[i].Upper;v++){x[i]=v;Visit(i+1);}
            }Visit(0);
            var r=GeneralFlow.Solve(m,true);if(!NetworkVerifier.Verify(m,r,true)||r.Value!=best||r.Status!=(best.HasValue?"Optimal":"Infeasible"))throw new Exception("finite");
            Console.WriteLine(JsonSerializer.Serialize(new{kind="finite",h,model=m,result=r,expected=best},CertifiedQuotient.Json));finite++;if(!best.HasValue)infeasible++;
            void Reject(string name,NetworkDecision bad){if(NetworkVerifier.Verify(m,bad,true))throw new Exception(name);tamper++;Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",name,rejected=true}));}
            if(r.Flow!=null){Reject("wrong-cost",r with{Value=r.Value+1});Reject("missing-potential",r with{Potential=null});var bad=r.Flow.ToArray();bad[0]=a[0].Upper+1;Reject("capacity",r with{Flow=bad});}
            else{Reject("empty-cut",r with{Cut=[]});Reject("full-cut",r with{Cut=[0,1,2]});Reject("missing-cut",r with{Cut=null});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",finite,vectors,tamper,infeasible}));
    }
}
