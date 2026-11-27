using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace WowDi.Studio.HostQualification;

// Bundled assets only. Log names of known local qualification resources,
// not arbitrary URI strings, query parameters, credentials or contents.
internal sealed class QualificationFileProvider : IFileProvider, IDisposable
{
    private readonly PhysicalFileProvider inner = new(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
    public IFileInfo GetFileInfo(string subpath)
    {
        var file = inner.GetFileInfo(subpath);
        var name = subpath.TrimStart('/');
        if (name is "index.html" or "probe.js" or "_framework/blazor.webview.js")
            Console.WriteLine($"Local qualification resource {name}: found={file.Exists}");
        return file;
    }
    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);
    public IChangeToken Watch(string filter) => inner.Watch(filter);
    public void Dispose() => inner.Dispose();
}
