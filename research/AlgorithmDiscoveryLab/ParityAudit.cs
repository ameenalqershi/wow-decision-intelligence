using ComponentStudy;
using System.Text.Json;

namespace AlgorithmDiscoveryLab;
internal static class ParityAudit
{
    public static void Run()
    {
        const int n=4;
        var edges = (from i in Enumerable.Range(0,n) from j in Enumerable.Range(i+1,n-i-1)
                     select new Capacity((1<<i)|(1<<j),1,1)).ToArray();
        int checks=0, inconsistent=0, strictBox=0, strictPair=0;
        for(int graph=0;graph<(1<<edges.Length);graph++)
        {
            var rows=edges.Where((_,i)=>(graph & (1<<i))!=0).ToArray();
            for(int code=0;code<625;code++)
            {
                int t=code;
                var d=new long[n];
                for(int i=0;i<n;i++){d[i]=t%5-2;t/=5;}
                long? expected=null;
                for(int mask=0;mask<(1<<n);mask++)
                {
                    var bits=Enumerable.Range(0,n).Select(i=>(mask/(1<<i))%2).ToArray();
                    if(rows.Any(r=>Enumerable.Range(0,n).Where(i=>(r.Scope/(1<<i))%2==1).Sum(i=>bits[i])!=1)) continue;
                    long value=Enumerable.Range(0,n).Sum(i=>bits[i]*d[i]);
                    expected=expected is long old?Math.Min(old,value):value;
                }
                var actual=ParityFloor.Minimum(d,rows);
                if(actual!=expected || ParityFloor.Minimum(d,rows.Reverse().Concat(rows).ToArray())!=expected)
                    throw new InvalidOperationException(JsonSerializer.Serialize(new {graph,code,d,rows,expected,actual}));
                if(expected is long exact)
                {
                    long box=d.Sum(v=>Math.Min(0,v));
                    long pair=0; int used=0;
                    foreach(var r in rows)
                    {
                        if((r.Scope & used)!=0)continue;
                        pair+=Enumerable.Range(0,n).Where(i=>(r.Scope&(1<<i))!=0).Min(i=>d[i]); used|=r.Scope;
                    }
                    for(int i=0;i<n;i++) if((used&(1<<i))==0) pair+=Math.Min(0,d[i]);
                    if(exact<pair || pair<box) throw new Exception("Dominance violation");
                    if(exact>pair)strictPair++;
                    if(exact>box)strictBox++;
                } else inconsistent++;
                checks++;
            }
        }
        long extreme=(long)int.MaxValue-int.MinValue;
        if(ParityFloor.Minimum([extreme,-extreme], [new(3,1,1)])!=-extreme)
            throw new Exception("Coefficient subtraction boundary failed");
        // Large but individually safe revision deltas; the old Int32 candidate
        // cannot subtract these, the parity challenger uses Int64.
        var arm=new ParityTransfer();
        var a=new Model(1,[],[new(1,int.MinValue)]);
        var b=new Model(1,[],[new(1,int.MaxValue)]);
        if(arm.Solve(a).Best?.Value!=int.MinValue || arm.Solve(b).Best?.Value!=0)
            throw new Exception("Extreme revision failed");
        // A source expansion must not transport even when the old witness survives.
        var expansion=new ParityTransfer();
        var fixedOne=new Model(1,[new(1,1,1)],[new(1,7)]);
        if(expansion.Solve(fixedOne).Best?.Value!=7 || expansion.Solve(fixedOne with {Capacities=[]}) is not {Transported:false,Best.Value:0})
            throw new Exception("Expansion guard failed");
        Console.WriteLine(JsonSerializer.Serialize(new {checks,inconsistent,strictBox,strictPair,duplicateAndOrderChecks=checks,boundaryCases=3,failures=0}));
    }
}
