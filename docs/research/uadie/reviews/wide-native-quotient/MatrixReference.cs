using System.Runtime.InteropServices;
internal static class MatrixReference
{
    internal static NetworkDecision Highs(IntegralMatrix m)
    {
        var start=new List<int>();var index=new List<int>();var values=new List<double>();
        foreach(var r in m.Rows){start.Add(index.Count);index.AddRange(r.Index);values.AddRange(r.Coeff.Select(a=>(double)a));}
        IntPtr h=Native.Highs_create();if(h==IntPtr.Zero)throw new Exception("HiGHS create");
        void Ok(int s){if(s<0)throw new Exception("HiGHS call");}
        try{
            Ok(Native.Highs_setBoolOptionValue(h,"output_flag",0));Ok(Native.Highs_setIntOptionValue(h,"threads",1));
            Ok(Native.Highs_setDoubleOptionValue(h,"time_limit",.5));Ok(Native.Highs_setDoubleOptionValue(h,"mip_rel_gap",0));Ok(Native.Highs_setDoubleOptionValue(h,"mip_abs_gap",0));
            Ok(Native.Highs_passMip(h,m.N,m.Rows.Length,index.Count,2,1,0,MatrixAdmission.MinCost(m).Select(x=>(double)x).ToArray(),m.Lower.Select(x=>(double)x).ToArray(),m.Upper.Select(x=>(double)x).ToArray(),m.Rows.Select(r=>(double)r.L).ToArray(),m.Rows.Select(r=>(double)r.U).ToArray(),start.ToArray(),index.ToArray(),values.ToArray(),Enumerable.Repeat(1,m.N).ToArray()));
            Ok(Native.Highs_run(h));int st=Native.Highs_getModelStatus(h);Ok(Native.Highs_getIntInfoValue(h,"primal_solution_status",out int primal));
            if(st==8)return new("Infeasible",null,null,null,null);
            if(primal!=2)return new("Unknown",null,null,null,null);
            var raw=new double[m.N];Ok(Native.Highs_getSolution(h,raw,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero));
            long[] x=raw.Select(v=>{if(!double.IsFinite(v)||Math.Abs(v-Math.Round(v))>1e-6)throw new Exception("integrality");return checked((long)Math.Round(v));}).ToArray();
            long z=MatrixAdmission.Value(m,x),value=m.Sense=="min"?z-m.Offset:m.Offset-z;
            Ok(Native.Highs_getDoubleInfoValue(h,"objective_function_value",out double objective));
            if(!double.IsFinite(objective)||Math.Abs(objective-value)>1e-6)throw new Exception("HiGHS objective");
            return new(st==7?"Optimal":"Feasible",value,x,null,null);
        }finally{Native.Highs_destroy(h);}
    }
    private static class Native
    {
        const string Lib="highs.so";
        [DllImport(Lib)]internal static extern IntPtr Highs_create();
        [DllImport(Lib)]internal static extern void Highs_destroy(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_setBoolOptionValue(IntPtr h,string name,int value);
        [DllImport(Lib)]internal static extern int Highs_setIntOptionValue(IntPtr h,string name,int value);
        [DllImport(Lib)]internal static extern int Highs_setDoubleOptionValue(IntPtr h,string name,double value);
        [DllImport(Lib)]internal static extern int Highs_passMip(IntPtr h,int n,int m,int nnz,int format,int sense,double offset,double[] cost,double[] cl,double[] cu,double[] rl,double[] ru,int[] start,int[] idx,double[] vals,int[] integer);
        [DllImport(Lib)]internal static extern int Highs_run(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getModelStatus(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getIntInfoValue(IntPtr h,string name,out int value);
        [DllImport(Lib)]internal static extern int Highs_getSolution(IntPtr h,[Out]double[] x,IntPtr cd,IntPtr rv,IntPtr rd);
        [DllImport(Lib)]internal static extern int Highs_getDoubleInfoValue(IntPtr h,string name,out double value);
    }
}
