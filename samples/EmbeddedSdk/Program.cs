using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WowDi.Contracts;
using WowDi.ModelingSdk;
using WowDi.Udir;

// Deterministic Batch manufacturing model, authored by the independent package consumer.
// No scenario support or native solve is asserted by this managed admission example.
var source=new ModelSource("batch","Capacity 10 kg; consumption 2 kg/batch; margin 4 USD/batch; quantity 0..8; maximize profit within capacity."u8.ToArray());
var catalog=new FrozenMeasurementCatalog([new("batch","batch","1"),new("kg","mass","1"),new("minute","time","1")],["USD"],["shift"]);
var builder=new ModelBuilder("consumer","batch","r1",new("shift","UTC","2026-01-01T00:00:00Z","minute"),catalog,source);
var batch=builder.Integer<Batch>("batch","batch");
var kg=builder.Integer<Kilogram>("kg","kg");
var usage=builder.Integer<KgPerBatch>("usage","kg/batch");
var margin=builder.Integer<UsdPerBatch>("margin","USD/batch");
var dollar=builder.Money<Dollar>("usd","USD");
var stock=builder.Resource("stock",kg,10,10,source.Locate("Capacity 10 kg"));
var material=builder.Parameter("consumption",usage,2,source.Locate("consumption 2 kg/batch"));
var price=builder.Parameter("margin",margin,4,source.Locate("margin 4 USD/batch"));
var qty=builder.Variable("qty",batch,0,8,source.Locate("quantity 0..8"));
var row=source.Locate("within capacity");
var goal=source.Locate("maximize profit");
builder.HardConstraint("available",builder.AtMost(builder.Multiply(builder.Reference(qty,row),builder.Reference(material,row),kg,row),
    builder.Reference(stock,row),row),row);
builder.Optimize("profit",builder.Multiply(builder.Reference(qty,goal),builder.Reference(price,goal),dollar,goal),Direction.Maximize,1,goal);
builder.RequireCapability("integer.linear");
builder.FreezeFacts();
var built=builder.Build();
var admitted=built.Normalize(catalog);
if(!admitted.Accepted||built.FactsDigest is null)throw new InvalidOperationException("Managed admission failed.");
var bytes=built.Document.Write();
var imported=ModelBuilder.Import(bytes,catalog.Semantics);
if(!bytes.AsSpan().SequenceEqual(imported.Write()))throw new InvalidOperationException("Round-trip bytes differ.");
var future=JsonNode.Parse(bytes)!.AsObject();
future["version"]="2.0";
try
{
    _=ModelBuilder.Import(System.Text.Encoding.UTF8.GetBytes(future.ToJsonString()),catalog.Semantics);
    throw new InvalidOperationException("Incompatible major version accepted.");
}
catch(ContractException e) when(e.Code=="contract.major"){Console.WriteLine("Version diagnostic: "+e.Code);}
using var cancelled=new CancellationTokenSource();cancelled.Cancel();
try
{
    _=built.Normalize(catalog,cancelled.Token);
    throw new InvalidOperationException("Cancellation ignored.");
}
catch(OperationCanceledException){Console.WriteLine("Cancellation: observed");}
Console.WriteLine("UDIR SHA256: "+Convert.ToHexStringLower(SHA256.HashData(bytes)));
Console.WriteLine("Normalized SHA256: "+admitted.Model!.SemanticDigest);
Console.WriteLine("MANAGED_ADMISSION_ONLY");

readonly struct Batch:IQuantity;
readonly struct Kilogram:IQuantity;
readonly struct KgPerBatch:IQuantity;
readonly struct UsdPerBatch:IQuantity;
readonly struct Dollar:IQuantity;
