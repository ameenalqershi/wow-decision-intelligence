using WowDi.ModelingSdk;
using WowDi.Samples;

if (args.Length != 1 || args[0] is not ("production" or "workforce"))
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/ModelingSdk -- production|workforce");
    return 2;
}
var source = new ModelSource(args[0], File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,args[0]+".txt")));
var model = args[0] == "production" ? ExampleModels.Production(source) : ExampleModels.Workforce(source);
var bytes=model.Document.Write();
_ = ModelBuilder.Import(bytes,ExampleModels.Catalog().Semantics);
using var stdout=Console.OpenStandardOutput();
stdout.Write(bytes);
return 0;
