using AlgorithmDiscoveryLab;
using ComponentStudy;
using System.Text.Json;
using System.Numerics;
int n=4;int[] edges=[3,5,9,6,10,12];
long checks=0,infeasible=0,reordered=0;
var candidate=new StructuralAdmission(true);
for(int g=0;g<64;g++)
for(int fix=0;fix<81;fix++)
{
    var rows=new List<Capacity>();
    for(int i=0;i<6;i++)if((g&(1<<i))!=0)rows.Add(new(edges[i],1,1));
    int rem=fix,zeros=0,ones=0;
    for(int i=0;i<n;i++){int state=rem%3;rem/=3;if(state==1)zeros|=1<<i;else if(state==2)ones|=1<<i;}
    if(zeros!=0)rows.Add(new(zeros,0,0));
    if(ones!=0){int w=BitOperations.PopCount((uint)ones);rows.Add(new(ones,w,w));}
    rows.Add(new(15,0,4));
    for(int c=0;c<81;c++)
    {
        rem=c;var costs=new Cost[n];
        for(int i=0;i<n;i++){costs[i]=new(1<<i,rem%3-1);rem/=3;}
        var model=new Model(n,rows.ToArray(),costs);
        int? expected=Oracle.Minimum(model);
        var answer=candidate.Solve(AdaptiveDecisionStudy.Admit(model));
        bool valid=candidate.Route=="direct"&&answer.Best?.Value==expected&&(answer.Best==null||Oracle.Evaluate(model,answer.Best.Mask)==expected);
        if(!valid){Console.WriteLine(JsonSerializer.Serialize(new{valid,model,expected,answer}));throw new Exception("finite mismatch");}
        checks++;if(expected==null)infeasible++;
        if(c==40)
        {
            var mutated=model with {Capacities=rows.AsEnumerable().Reverse().Concat(rows.Take(1)).ToArray()};
            var r=candidate.Solve(AdaptiveDecisionStudy.Admit(mutated));
            if(r.Best?.Value!=expected||(r.Best!=null&&Oracle.Evaluate(mutated,r.Best.Mask)!=expected))throw new Exception("reorder mismatch");
            reordered++;
        }
    }
}
var conflict=new Model(4,[new(3,0,0),new(2,1,1)],[new(1,0),new(2,0),new(4,0),new(8,0)]);
if(candidate.Solve(AdaptiveDecisionStudy.Admit(conflict)).Best!=null)throw new Exception("conflicting force");
Console.WriteLine(JsonSerializer.Serialize(new{checks,infeasible,reordered,explicitConflicts=1,failures=0,valid=true}));
