using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WowDi.Contracts;
using WowDi.Udir;

namespace WowDi.Manufacturing.Data;

public static class FactoryAdmission
{
    public const string Profile = "wowdi.factory.source/1";
    public const int MaxBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32, NumberHandling = JsonNumberHandling.Strict
    };

    public static FrozenFactoryDataset Read(ReadOnlySpan<byte> utf8)
    {
        Need(utf8.Length is > 0 and <= MaxBytes, "size", "$", "Expected a bounded UTF-8 source.");
        // Freeze before parsing; the caller cannot mutate the admitted bytes.
        var bytes = utf8.ToArray();
        FactoryDataset? model;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            UniqueMembers(document.RootElement, "$");
            model = JsonSerializer.Deserialize<FactoryDataset>(bytes, Options);
        }
        catch (JsonException ex) { throw Error("json", ex.Path ?? "$", "Invalid source profile or missing required member."); }
        Need(model is not null, "json", "$", "A source object is required.");
        Validate(model!);
        return new(model!, ImmutableArray.CreateRange(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static void UniqueMembers(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Need(names.Add(property.Name), "duplicate-member", path, "Duplicate JSON member.");
                UniqueMembers(property.Value, path + "." + property.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in value.EnumerateArray()) UniqueMembers(item, path + "[" + i++ + "]");
        }
    }

    public static void Validate(FactoryDataset model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Need(model.Profile == Profile, "profile", "$.profile", "Unsupported source profile.");
        Id(model.RevisionId, "$.revisionId");
        Need(model.Clock is not null, "clock", "$.clock", "Clock required.");
        var clock = model.Clock!;
        Id(clock.CalendarId, "$.clock.calendarId"); Id(clock.TimeZoneId, "$.clock.timeZoneId");
        Need(clock.TickUnit == "minute", "tick-unit", "$.clock.tickUnit", "Expected elapsed minute.");
        Need(DateTimeOffset.TryParseExact(clock.Origin, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var origin),
            "origin", "$.clock.origin", "Expected explicit UTC second precision.");
        Need(clock.HorizonMinutes > 0 && clock.HorizonMinutes <= (DateTimeOffset.MaxValue - origin).TotalMinutes,
            "horizon", "$.clock.horizonMinutes", "Expected positive representable horizon.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(clock.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw Error("timezone", "$.clock.timeZoneId", "Unknown time zone."); }
        Id(model.ObjectiveUnit, "$.objectiveUnit");
        if (model.Currency is not null)
        {
            Need(model.Currency.Length == 3 && model.Currency.All(char.IsAsciiLetterUpper),
                "currency", "$.currency", "Expected a three-letter uppercase currency code.");
            Need(model.ObjectiveUnit == model.Currency, "currency", "$.objectiveUnit", "Objective unit must match the single declared currency.");
        }
        else Need(model.ObjectiveUnit == "weighted-minute", "objective-unit", "$.objectiveUnit",
            "Nonmonetary pilot profile uses weighted-minute; currency is not inferred.");
        Array(model.Calendars,"$.calendars"); Array(model.Materials,"$.materials"); Array(model.Receipts,"$.receipts");
        Array(model.Operations,"$.operations"); Array(model.Precedence,"$.precedence");
        Need(model.Calendars.Length > 0, "calendar", "$.calendars", "At least one explicit machine calendar required.");
        Need(model.Operations.Length > 0, "operations", "$.operations", "At least one mandatory operation required.");
        var machines = new HashSet<string>(StringComparer.Ordinal);
        for (int i=0;i<model.Calendars.Length;i++)
        {
            var p=$"$.calendars[{i}]";var row=model.Calendars[i];
            Need(row is not null,"row",p,"Calendar required."); Id(row!.MachineId,p+".machineId");
            Need(machines.Add(row.MachineId),"duplicate-machine",p+".machineId","Machine calendar must be unique.");
            Array(row.Downtime,p+".downtime");
            var intervals = new List<(Downtime Interval,int Index)>();
            for(int j=0;j<row.Downtime.Length;j++)
            {
                var d=row.Downtime[j];var q=p+$".downtime[{j}]";
                Need(d is not null,"row",q,"Downtime required.");
                Need(d!.StartMinute>=0 && d.StartMinute<d.EndMinute && d.EndMinute<=clock.HorizonMinutes,
                    "interval",q,"Expected nonempty half-open downtime within horizon.");
                intervals.Add((d,j));
            }
            var sorted=intervals.OrderBy(x=>x.Interval.StartMinute).ThenBy(x=>x.Interval.EndMinute).ToArray();
            for(int j=1;j<sorted.Length;j++)
                Need(sorted[j-1].Interval.EndMinute<=sorted[j].Interval.StartMinute,"calendar-overlap",
                    p+$".downtime[{sorted[j].Index}]","Machine downtime overlaps another interval.");
        }
        var materialUnits=new Dictionary<string,string>(StringComparer.Ordinal);
        for(int i=0;i<model.Materials.Length;i++)
        {
            var p=$"$.materials[{i}]";var row=model.Materials[i];
            Need(row is not null,"row",p,"Material required.");
            Id(row!.MaterialId,p+".materialId");Id(row.Unit,p+".unit");
            Need(materialUnits.TryAdd(row.MaterialId,row.Unit),"duplicate-material",p+".materialId","Material must be unique.");
            Nonnegative(row.InitialQuantity,p+".initialQuantity");
        }
        var receiptIds=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<model.Receipts.Length;i++)
        {
            var p=$"$.receipts[{i}]";var row=model.Receipts[i];
            Need(row is not null,"row",p,"Receipt required.");Id(row!.ReceiptId,p+".receiptId");
            Need(receiptIds.Add(row.ReceiptId),"duplicate-receipt",p+".receiptId","Receipt ID must be unique.");
            Material(row.MaterialId,row.Unit,p,materialUnits);Nonnegative(row.Quantity,p+".quantity");
            Need(row.AvailableMinute>=0&&row.AvailableMinute<=clock.HorizonMinutes,"receipt-time",p+".availableMinute","Receipt must be within horizon.");
        }
        var operations=new Dictionary<string,int>(StringComparer.Ordinal);
        for(int i=0;i<model.Operations.Length;i++)
        {
            var p=$"$.operations[{i}]";var row=model.Operations[i];
            Need(row is not null,"row",p,"Operation required.");Id(row!.OperationId,p+".operationId");Id(row.JobId,p+".jobId");
            Need(operations.TryAdd(row.OperationId,i),"duplicate-operation",p+".operationId","Operation must be unique.");
            Id(row.MachineId,p+".machineId");
            Need(machines.Contains(row.MachineId),"missing-calendar",p+".machineId","Machine requires an explicit calendar.");
            Need(row.DurationMinutes>0&&row.DurationMinutes<=clock.HorizonMinutes,"duration",p+".durationMinutes","Positive duration within horizon required.");
            Need(row.ReleaseMinute>=0&&row.ReleaseMinute<=clock.HorizonMinutes,"release",p+".releaseMinute","Release outside horizon.");
            Need(row.DueMinute>=0,"due",p+".dueMinute","Due minute must be nonnegative.");
            if(row.HardDeadlineMinute is int deadline)
                Need(deadline>=0,"deadline",p+".hardDeadlineMinute","Hard deadline must be nonnegative.");
            Nonnegative(row.TardinessWeight,p+".tardinessWeight");
            Array(row.Materials,p+".materials");
            var consumed=new HashSet<string>(StringComparer.Ordinal);
            for(int j=0;j<row.Materials.Length;j++)
            {
                var q=p+$".materials[{j}]";var item=row.Materials[j];
                Need(item is not null,"row",q,"Consumption required.");
                Material(item!.MaterialId,item.Unit,q,materialUnits);Nonnegative(item.Quantity,q+".quantity");
                Need(consumed.Add(item.MaterialId),"duplicate-consumption",q,"One consumption coefficient per material and operation.");
            }
        }
        var edges=new HashSet<(int,int)>();
        var outgoing=Enumerable.Range(0,operations.Count).Select(_=>new List<int>()).ToArray();
        var indegree=new int[operations.Count];
        for(int i=0;i<model.Precedence.Length;i++)
        {
            var p=$"$.precedence[{i}]";var edge=model.Precedence[i];
            Need(edge is not null,"row",p,"Precedence required.");
            Id(edge!.Predecessor,p+".predecessor");Id(edge.Successor,p+".successor");
            bool a=operations.TryGetValue(edge.Predecessor,out var from),b=operations.TryGetValue(edge.Successor,out var to);
            Need(a&&b,"precedence-reference",p,"Both operation references must exist.");
            Need(from!=to&&edges.Add((from,to)),"precedence-edge",p,"Self-edge or duplicate precedence.");
            outgoing[from].Add(to);indegree[to]++;
        }
        var ready=new Queue<int>(Enumerable.Range(0,indegree.Length).Where(i=>indegree[i]==0));int visited=0;
        while(ready.TryDequeue(out int v))
        { visited++;foreach(var next in outgoing[v])if(--indegree[next]==0)ready.Enqueue(next); }
        Need(visited==operations.Count,"precedence-cycle","$.precedence","Mandatory precedence must form an acyclic graph.");
    }

    private static void Material(string id,string unit,string p,Dictionary<string,string> units)
    {
        Id(id,p+".materialId");Id(unit,p+".unit");
        Need(units.TryGetValue(id,out var expected),"material-reference",p+".materialId","Unknown material.");
        Need(unit==expected,"unit-mismatch",p+".unit","Explicit upstream normalization required; no implicit conversion.");
    }
    private static void Nonnegative(string value,string p)
    {
        int sign;
        try { sign=ExactValues.Compare(value,"0"); }
        catch(ContractException){throw Error("exact-number",p,"Expected canonical UDIR integer or reduced rational string.");}
        Need(sign>=0,"negative",p,"Expected nonnegative quantity or weight.");
    }
    private static void Array<T>(ImmutableArray<T> value,string p)=>Need(!value.IsDefault,"missing-array",p,"Explicit array required; use [] for none.");
    private static void Id(string? value,string p)=>Need(!string.IsNullOrWhiteSpace(value)&&value.Length<=256&&!value.Any(char.IsControl),
        "identity",p,"Expected nonblank bounded ordinal identifier.");
    private static DatasetException Error(string code,string p,string message)=>new(new(code,p,message));
    private static void Need(bool condition,string code,string p,string message){if(!condition)throw Error(code,p,message);}
}
