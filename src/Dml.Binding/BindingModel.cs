using System.Collections.Immutable;
using WowDi.Dml.Syntax;
using WowDi.Udir;

namespace WowDi.Dml.Binding;

public enum DeclarationKind { Parameter, Resource, Decision, Constraint, Objective, Window, Source, Scenario, Policy }
public enum ReferenceKind { Expression, Override, Trigger, Dependency, Policy }

public sealed record AuthorizedSource(string Alias,string Schema);
public sealed class BindingContext
{
    public ImmutableDictionary<string,AuthorizedSource> Sources {get;}
    public BindingContext(IEnumerable<AuthorizedSource>? sources=null)
    {
        var builder=ImmutableDictionary.CreateBuilder<string,AuthorizedSource>(StringComparer.Ordinal);
        foreach(var source in sources??[])
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Alias);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Schema);
            if(!builder.TryAdd(source.Alias,source))throw new ArgumentException("Duplicate authorized source alias.",nameof(sources));
        }
        Sources=builder.ToImmutable();
    }
}

public sealed record BoundDeclaration(string Name,DeclarationKind Kind,DeclarationSyntax Syntax,SourceSpan NameSpan);
public sealed record BoundReference(ReferenceKind Kind,SourceSpan UseSpan,BoundDeclaration Target);
public sealed record BindingDiagnostic(string Code,string Message,SourceSpan Span,int Line,int Column,
    SourceSpan? RelatedSpan,ImmutableArray<string> Suggestions);

public sealed class BoundDocument
{
    public SourceDocument Source {get;}
    public ImmutableArray<byte> SourceBytes {get;}
    public ProblemSyntax Syntax {get;}
    public ImmutableDictionary<string,BoundDeclaration> Declarations {get;}
    public ImmutableArray<BoundReference> References {get;}
    public ImmutableArray<BoundDeclaration> ValueOrder {get;}
    internal BoundDocument(SourceDocument source,ImmutableArray<byte> bytes,ProblemSyntax syntax,
        ImmutableDictionary<string,BoundDeclaration> declarations,ImmutableArray<BoundReference> references,
        ImmutableArray<BoundDeclaration> valueOrder)
        =>(Source,SourceBytes,Syntax,Declarations,References,ValueOrder)=(source,bytes,syntax,declarations,references,valueOrder);
}

public sealed record BindingResult(BoundDocument? Document,BindingDiagnostic? Diagnostic)
{
    public bool Accepted=>Document is not null&&Diagnostic is null;
}
