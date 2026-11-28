using System.Diagnostics;
using Highs;
using WowDi.Research.Sparse;

namespace ScaleBench;

/// <summary>RESEARCH harness: passes a SparseModel to HiGHS 1.16. The engine receives binary64 data (rationals rounded to
/// nearest); exactness is restored afterwards by the independent sparse verifier and the exact safe bound.</summary>
public static class HighsBridge
{
    public sealed record Run(string Status,double? Objective,double? DualBound,double[]? X,double[]? RowDual,double Seconds,bool Mip);

    public static Run Solve(SparseModel m,bool relax,double timeLimit,int threads)
    {
        const double inf=1e30;
        double D(int i,double dflt)=>i<0?dflt:m.Pool[i].ToDouble();
        var cost=m.Objective.Select(i=>D(i,0)).ToArray();
        var model=new HighsModel(cost,m.ColLower.Select(i=>D(i,-inf)).ToArray(),m.ColUpper.Select(i=>D(i,inf)).ToArray(),
            m.RowLower.Select(i=>D(i,-inf)).ToArray(),m.RowUpper.Select(i=>D(i,inf)).ToArray(),m.RowStart[..m.Rows],m.ColIndex,
            m.ValueIndex.Select(i=>m.Pool[i].ToDouble()).ToArray(),relax?null:m.Integer.Select(b=>b?1:0).ToArray(),
            m.ObjectiveOffset<0?0:m.Pool[m.ObjectiveOffset].ToDouble(),HighsMatrixFormat.kRowwise,
            m.Maximize?HighsObjectiveSense.kMaximize:HighsObjectiveSense.kMinimize);
        using var h=new HighsLpSolver();
        h.setBoolOptionValue("output_flag",0);
        h.setDoubleOptionValue("time_limit",timeLimit);
        h.setIntOptionValue("threads",threads);
        h.setIntOptionValue("random_seed",1);
        if(!relax)h.setDoubleOptionValue("mip_rel_gap",0.0);
        var mip=!relax&&m.Integer.Any(b=>b);
        var st=Stopwatch.StartNew();
        var pass=mip?h.passMip(model):h.passLp(model);
        if(pass==HighsStatus.kError)return new("passError",null,null,null,null,0,mip);
        h.run();
        var secs=st.Elapsed.TotalSeconds;
        var ms=h.GetModelStatus();var info=h.getInfo();var sol=h.getSolution();
        var x=sol.colvalue is {Length:>0} cv&&cv.Length==m.Columns&&cv.All(double.IsFinite)&&ms is not HighsModelStatus.kInfeasible?cv:null;
        var duals=!mip&&sol.rowdual is {Length:>0} rd&&rd.Length==m.Rows?rd:null;
        double? obj=x is null?null:info.ObjectiveValue;double? db=mip&&double.IsFinite(info.DualBound)?info.DualBound:null;
        return new(ms.ToString(),obj,db,x,duals,secs,mip);
    }
}
