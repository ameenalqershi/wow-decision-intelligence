using System.Diagnostics;
using System.Text.Json;
internal static class WeightedStudy
{
    internal static WeightedModel Model(int n,int seed,string family,int revision)
    {
        var rng=new Random(seed+n*1009+revision*10007);var planted=Enumerable.Range(0,n).Select(_=>rng.Next(2)).ToArray();
        var rows=new List<WeightedRow>();
        int step=family=="overlap"?4:8;
        for(int i=0;i<n;i+=step){
            var idx=Enumerable.Range(i,Math.Min(8,n-i)).ToArray();
            long g=family=="coprime"?1:rng.Next(2,20);
            var a=idx.Select(_=>g*(long)(rng.Next(1,9)*(rng.Next(2)==0?-1:1))).ToArray();
            if(family=="coprime")a[0]=1;
            long z=idx.Select((x,j)=>a[j]*planted[x]).Sum();
            long l=z-rng.Next(0,4),u=z+rng.Next(0,4);
            if(family=="redundant"&&i%16==0){l=a.Where(c=>c<0).Sum()-1;u=a.Where(c=>c>0).Sum()+1;}
            rows.Add(new(idx,a,l,u));
        }
        if(family=="contradiction")rows.Add(new([0],[6],1,5));
        return new(n,rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-50,51)).ToArray());
    }
    internal static void Run()
    {
        foreach(int n in new[]{64,256})
        foreach(string family in new[]{"gcd","coprime","overlap","redundant","contradiction"})
        for(int h=0;h<12;h++)
        for(int rev=0;rev<2;rev++){
            int seed=2970101+h;var m=WeightedReduction.Admit(Model(n,seed,family,rev));
            var results=new List<WeightedDecision>();
            for(int a=0;a<2;a++){
                bool reduce=(h+rev+a)%2==1;string arm=reduce?"G21":"original";
                long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();var trace=new PhaseTrace();
                string d=WeightedReduction.Digest(m);trace.Mark("digest");
                var r=WeightedReduction.Decide(m,reduce);trace.Mark("solve");
                WeightedVerifier.Replay(m,d,r);trace.Mark("replay");
                byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new{digest=d,model=m,decision=r},CertifiedQuotient.Json);trace.Mark("encode");
                long ticks=Stopwatch.GetTimestamp()-start;results.Add(r);
                Console.WriteLine(JsonSerializer.Serialize(new{n,family,seed,rev,arm,digest=d,status=r.Result.Status,value=r.Result.Value,formalInfeasible=r.InfeasibleProved,rows=m.Rows.Length,normalizedRows=r.Result.Rows,route=r.Route,dropped=r.Proof?.Rows.Count(p=>p.Drop)??0,gcdRows=r.Proof?.Rows.Count(p=>p.Gcd>1)??0,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=bytes.Length,phases=trace.Ticks}));
            }
            var x=results[0].Result;var y=results[1].Result;
            if(x.Status=="Optimal"&&y.Status=="Optimal"&&x.Value!=y.Value||x.Status=="Infeasible"&&y.Bits!=null||y.Status=="Infeasible"&&x.Bits!=null)throw new Exception("differential");
        }
    }
    internal static void Audit()
    {
        int finite=0,assignments=0,tamper=0,contradiction=0;
        for(int h=0;h<96;h++){
            string family=new[]{"gcd","coprime","overlap","redundant","contradiction"}[h%5];
            var m=WeightedReduction.Admit(Model(8,3000101+h,family,0));string d=WeightedReduction.Digest(m);var c=WeightedReduction.Produce(m,d);
            if(!WeightedVerifier.Verify(m,d,c))throw new Exception("proof");
            WeightedModel? q=c.Rows.Any(p=>p.Impossible)?null:WeightedReduction.Apply(m,c);long? best=null;
            bool Feasible(WeightedModel x,int mask){
                foreach(var r in x.Rows){long sum=0;for(int i=0;i<r.Index.Length;i++)if(((mask>>r.Index[i])&1)!=0)sum+=r.Coeff[i];if(sum<r.L||sum>r.U)return false;}
                return true;
            }
            for(int mask=0;mask<256;mask++){
                bool original=Feasible(m,mask),reduced=q!=null&&Feasible(q,mask);
                if(original!=reduced)throw new Exception("feasible set mismatch");assignments++;
                if(original){long cost=Enumerable.Range(0,m.N).Where(i=>((mask>>i)&1)!=0).Sum(i=>m.Cost[i]);if(best==null||cost<best)best=cost;}
            }
            var o=WeightedReduction.Decide(m,true);WeightedVerifier.Replay(m,d,o);
            if(o.Result.Status!=(best.HasValue?"Optimal":"Infeasible")||o.Result.Value!=best)throw new Exception("optimum");
            if(o.InfeasibleProved)contradiction++;
            Console.WriteLine(JsonSerializer.Serialize(new{kind="finite",h,m,c,o,best},CertifiedQuotient.Json));finite++;
            void Reject(string name,WeightedCertificate bad){
                if(WeightedVerifier.Verify(m,d,bad))throw new Exception(name);tamper++;
                Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",h,name,rejected=true}));
            }
            Reject("digest",c with{Digest="stale"});
            var rows=c.Rows.ToArray();rows[0]=rows[0] with{L=rows[0].L+1};Reject("rounding",c with{Rows=rows});
            rows=c.Rows.ToArray();rows[0]=rows[0] with{Drop=!rows[0].Drop};Reject("drop",c with{Rows=rows});
            rows=c.Rows.ToArray();rows[0]=rows[0] with{Impossible=!rows[0].Impossible};Reject("infeasibility",c with{Rows=rows});
            rows=c.Rows.ToArray();rows[0]=rows[0] with{Gcd=0};Reject("divisor",c with{Rows=rows});
        }
        bool overflow=false;try{WeightedReduction.Admit(new(1,[new([0],[long.MinValue],0,0)],[0]));}catch(OverflowException){overflow=true;}
        if(!overflow)throw new Exception("overflow");
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",finite,assignments,tamper,contradiction,overflow}));
    }
}
