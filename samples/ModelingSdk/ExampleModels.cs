using WowDi.ModelingSdk;
using WowDi.Udir;

namespace WowDi.Samples;

public static class ExampleModels
{
    public readonly struct Batch : IQuantity;
    public readonly struct Kilogram : IQuantity;
    public readonly struct KgPerBatch : IQuantity;
    public readonly struct UsdPerBatch : IQuantity;
    public readonly struct Dollar : IQuantity;
    public readonly struct Employee : IQuantity;
    public readonly struct Hour : IQuantity;
    public readonly struct HoursPerEmployee : IQuantity;
    public readonly struct UsdPerHour : IQuantity;

    public static FrozenMeasurementCatalog Catalog() => new(
        [new("batch","batch","1"),new("kg","mass","1"),new("employee","people","1"),
         new("hour","time","60"),new("minute","time","1")], ["USD"], ["shift"]);
    public static TimeBasis Basis() => new("shift","UTC","2026-01-01T00:00:00Z","minute");

    public static BuiltModel Production(ModelSource source)
    {
        var b = new ModelBuilder("example","production","r1",Basis(),Catalog(),source);
        var count = b.Integer<Batch>("batch","batch");
        var kg = b.Integer<Kilogram>("kg","kg");
        var consumption = b.Integer<KgPerBatch>("kg_per_batch","kg/batch");
        var margin = b.Integer<UsdPerBatch>("usd_per_batch","USD/batch");
        var usd = b.Money<Dollar>("usd","USD");
        var stockSpan=source.Locate("Stock capacity: 10 kg, with shortage scenario of 4 kg.");
        var materialSpan=source.Locate("Material consumption: 2 kg per batch.");
        var marginSpan=source.Locate("Contribution margin: 4 USD per batch.");
        var decisionSpan=source.Locate("Batch decision: integer between zero and eight.");
        var limitSpan=source.Locate("Material constraint: total consumption at most stock.");
        var profitSpan=source.Locate("Profit objective: maximize contribution margin times batches.");
        var stock=b.Resource("stock",kg,4,10,stockSpan);
        var material=b.Parameter("consumption",consumption,2,materialSpan);
        var price=b.Parameter("margin",margin,4,marginSpan);
        var qty=b.Variable("qty",count,0,8,decisionSpan);
        var total=b.Multiply(b.Reference(qty,limitSpan),b.Reference(material,limitSpan),kg,limitSpan);
        b.HardConstraint("available",b.AtMost(total,b.Reference(stock,limitSpan),limitSpan),limitSpan);
        var revenue=b.Multiply(b.Reference(qty,profitSpan),b.Reference(price,profitSpan),usd,profitSpan);
        b.Optimize("profit",revenue,Direction.Maximize,1,profitSpan);
        b.AddScenario("root",0,"base",source.Locate("Scenario root: stock is 10 kg before shortage observation."),null,null,b.Override(stock,10));
        b.FreezeFacts(b.Fact(stock,10));
        b.AddScenario("shortage",1,"shortage",source.Locate("Shortage branch: stock becomes 4 kg."),"root",null,b.Override(stock,4));
        b.AddScenario("normal",1,"normal",source.Locate("Normal branch: stock remains 10 kg."),"root",null,b.Override(stock,10));
        b.RequireCapability("integer.linear");
        b.RequireCapability("staged.uncertainty");
        return b.Build();
    }

    public static BuiltModel Workforce(ModelSource source)
    {
        var b = new ModelBuilder("example","workforce","r1",Basis(),Catalog(),source);
        var staffType=b.Integer<Employee>("employee","employee");
        var perStaff=b.Rate<HoursPerEmployee>("hours_per_employee","hour/employee");
        var hour=b.Duration<Hour>("hour","hour");
        var wage=b.Rate<UsdPerHour>("usd_per_hour","USD/hour");
        var usd=b.Money<Dollar>("usd","USD");
        var staff=b.Variable("staff",staffType,0,12,source.Locate("Staff decision: integer between zero and twelve employees."));
        var hours=b.Parameter("hoursPerStaff",perStaff,8,source.Locate("Hours per employee: 8 hours."));
        var price=b.Parameter("wage",wage,15,source.Locate("Hourly wage: 15 USD per hour."));
        var demand=b.Parameter("demand",hour,64,source.Locate("Demand: at least 64 hours."));
        var totalSpan=source.Locate("Labor capacity expression: staff times hours per employee.");
        var total=b.Multiply(b.Reference(staff,totalSpan),b.Reference(hours,totalSpan),hour,totalSpan);
        var coverage=source.Locate("Coverage constraint: total hours meet demand.");
        b.HardConstraint("coverage",b.AtLeast(total,b.Reference(demand,coverage),coverage),coverage);
        var costSpan=source.Locate("Cost expression: total hours times hourly wage.");
        var cost=b.Multiply(total,b.Reference(price,costSpan),usd,costSpan);
        b.Optimize("cost",cost,Direction.Minimize,1,source.Locate("Cost objective: minimize total wages."));
        b.RequireCapability("integer.linear");
        b.FreezeFacts();
        return b.Build();
    }
}
