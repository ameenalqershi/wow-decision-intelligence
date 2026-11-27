using System.Collections.Immutable;
using System.Text;
using WowDi.Dml.Syntax;
using WowDi.Udir;

namespace WowDi.Dml.Binding;

/// <summary>Pure, bounded binding of source bytes to a frozen symbol table. No connector access or evaluation.</summary>
public static class DmlBinder
{
    public static BindingResult Bind(string sourceId,ReadOnlyMemory<byte> utf8,BindingContext context,
        string grammarVersion=DmlParser.GrammarVersion,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        // Let the parser reject over-limit input before copying it.
        if(utf8.Length>DmlParser.MaximumBytes)
        {
            var rejected=DmlParser.Parse(sourceId,utf8,grammarVersion,cancellationToken);
            return FromSyntax(rejected.Diagnostic!);
        }
        var bytes=utf8.ToArray();
        var parsed=DmlParser.Parse(sourceId,bytes,grammarVersion,cancellationToken);
        if(!parsed.Accepted)return FromSyntax(parsed.Diagnostic!);
        var worker=new Binder(parsed.Problem!,context,cancellationToken);
        try
        {
            var (declarations,references,order)=worker.Bind();
            return new(new(parsed.Source!,ImmutableArray.CreateRange(bytes),parsed.Problem!,declarations,references,order),null);
        }
        catch(BindFailure error)
        {
            var text=Encoding.UTF8.GetString(bytes);
            int line=1,column=1;
            for(int i=0;i<error.Span.Start;i++)
            {
                if(text[i]=='\r'){line++;column=1;if(i+1<error.Span.Start&&text[i+1]=='\n')i++;}
                else if(text[i]=='\n'){line++;column=1;}
                else column++;
            }
            return new(null,new(error.Code,error.Message,error.Span,line,column,error.Related,error.Suggestions));
        }
    }
    private static BindingResult FromSyntax(DmlDiagnostic d)=>new(null,new(d.Code,d.Message,d.Span,d.Line,d.Column,null,[]));

    private sealed class BindFailure(string code,string message,SourceSpan span,SourceSpan? related=null,
        ImmutableArray<string> suggestions=default):Exception(message)
    {
        internal string Code {get;}=code;
        internal SourceSpan Span {get;}=span;
        internal SourceSpan? Related {get;}=related;
        internal ImmutableArray<string> Suggestions {get;}=suggestions.IsDefault?[]:suggestions;
    }

    private sealed class Binder(ProblemSyntax problem,BindingContext context,CancellationToken cancellation)
    {
        private readonly Dictionary<string,BoundDeclaration> declarations=new(StringComparer.Ordinal);
        private readonly ImmutableArray<BoundReference>.Builder references=ImmutableArray.CreateBuilder<BoundReference>();
        private readonly Dictionary<string,List<BoundReference>> valueEdges=new(StringComparer.Ordinal);
        private readonly Dictionary<string,List<BoundReference>> operationalEdges=new(StringComparer.Ordinal);
        private readonly HashSet<(string,string)> dependencies=[];
        private readonly HashSet<string> triggers=new(StringComparer.Ordinal);
        private static bool IsValue(DeclarationKind kind)=>kind is DeclarationKind.Parameter or DeclarationKind.Resource or DeclarationKind.Decision;
        private static bool IsAssignable(DeclarationKind kind)=>kind is DeclarationKind.Parameter or DeclarationKind.Resource;

        internal (ImmutableDictionary<string,BoundDeclaration>,ImmutableArray<BoundReference>,ImmutableArray<BoundDeclaration>) Bind()
        {
            foreach(var syntax in problem.Declarations)
            {
                cancellation.ThrowIfCancellationRequested();
                (Token Name,DeclarationKind Kind)? entry=syntax switch
                {
                    ValueDeclaration v=>(v.Name,v.Role.Kind==TokenKind.Param?DeclarationKind.Parameter:DeclarationKind.Resource),
                    DecisionDeclaration v=>(v.Name,DeclarationKind.Decision),
                    ConstraintDeclaration v=>(v.Name,DeclarationKind.Constraint),
                    ObjectiveDeclaration v=>(v.Name,DeclarationKind.Objective),
                    WindowDeclaration v=>(v.Name,DeclarationKind.Window),
                    SourceDeclaration v=>(v.Name,DeclarationKind.Source),
                    ScenarioDeclaration v=>(v.Name,DeclarationKind.Scenario),
                    PolicyDeclaration v=>(v.Name,DeclarationKind.Policy),
                    _=>null
                };
                if(entry is not {} e)continue;
                var item=new BoundDeclaration(e.Name.Text,e.Kind,syntax,e.Name.Span);
                if(!declarations.TryAdd(item.Name,item))
                    throw new BindFailure("DML-BIND-002","Duplicate declaration.",item.NameSpan,declarations[item.Name].NameSpan);
                if(IsValue(item.Kind))valueEdges.Add(item.Name,[]);
                if(item.Kind is DeclarationKind.Resource or DeclarationKind.Decision or DeclarationKind.Window)
                    operationalEdges.Add(item.Name,[]);
            }
            foreach(var syntax in problem.Declarations)
            {
                cancellation.ThrowIfCancellationRequested();
                switch(syntax)
                {
                    case ValueDeclaration v: BindExpression(v.Value,v.Name.Text);break;
                    case DecisionDeclaration v:
                        if(v.Lower is not null)BindExpression(v.Lower,v.Name.Text);
                        if(v.Upper is not null)BindExpression(v.Upper,v.Name.Text);
                        break;
                    case ConstraintDeclaration v:BindExpression(v.Left);BindExpression(v.Right);break;
                    case ObjectiveDeclaration v:BindExpression(v.Value);break;
                    case SourceDeclaration v:
                        if(!context.Sources.TryGetValue(v.Name.Text,out var allowed)||allowed.Schema!=v.Schema.Text)
                            throw new BindFailure("DML-BIND-006","Source alias or schema is not authorized.",v.Name.Span);
                        break;
                    case TriggerDeclaration v:
                        var target=Resolve(v.Source,ReferenceKind.Trigger,k=>k==DeclarationKind.Source);
                        if(!triggers.Add(target.Target.Name))
                            throw new BindFailure("DML-BIND-007","Duplicate trigger.",v.Source.Span,target.Target.NameSpan);
                        break;
                    case ScenarioDeclaration v:
                        var seen=new HashSet<string>(StringComparer.Ordinal);
                        foreach(var item in v.Overrides)
                        {
                            var reference=Resolve(item.Name,ReferenceKind.Override,IsAssignable);
                            if(!seen.Add(reference.Target.Name))
                                throw new BindFailure("DML-BIND-007","Duplicate scenario override.",item.Name.Span,reference.Target.NameSpan);
                            BindExpression(item.Value);
                        }
                        break;
                    case DependencyDeclaration v:
                        var a=Resolve(v.Name,ReferenceKind.Dependency,k=>k is DeclarationKind.Resource or DeclarationKind.Decision or DeclarationKind.Window);
                        var b=Resolve(v.DependsOn,ReferenceKind.Dependency,k=>k is DeclarationKind.Resource or DeclarationKind.Decision or DeclarationKind.Window);
                        if(!dependencies.Add((a.Target.Name,b.Target.Name)))
                            throw new BindFailure("DML-BIND-007","Duplicate operational dependency.",v.DependsOn.Span,a.Target.NameSpan);
                        operationalEdges[a.Target.Name].Add(b);
                        break;
                    case PolicyDeclaration v:
                        var row=Resolve(v.Constraint,ReferenceKind.Policy,k=>k==DeclarationKind.Constraint);
                        if(((ConstraintDeclaration)row.Target.Syntax).Penalty is not null)
                            throw new BindFailure("DML-BIND-008","A hard policy cannot refer to a soft constraint.",v.Constraint.Span,row.Target.NameSpan);
                        break;
                }
            }
            var order=Order(valueEdges,"DML-BIND-004","Cyclic value dependency.");
            _=Order(operationalEdges,"DML-BIND-005","Cyclic operational dependency.");
            return(declarations.ToImmutableDictionary(StringComparer.Ordinal),references.ToImmutable(),order);
        }

        private BoundReference Resolve(Token token,ReferenceKind kind,Func<DeclarationKind,bool> allowed)
        {
            if(!declarations.TryGetValue(token.Text,out var target))
                throw new BindFailure("DML-BIND-001","Unknown identifier.",token.Span,suggestions:Suggest(token.Text,allowed));
            if(!allowed(target.Kind))
                throw new BindFailure("DML-BIND-003","Identifier has the wrong declaration kind.",token.Span,target.NameSpan);
            var reference=new BoundReference(kind,token.Span,target);
            references.Add(reference);
            return reference;
        }

        private void BindExpression(ExpressionSyntax expression,string? owner=null)
        {
            var stack=new Stack<ExpressionSyntax>();stack.Push(expression);
            while(stack.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                switch(node)
                {
                    case ReferenceSyntax v:
                        var reference=Resolve(v.Name,ReferenceKind.Expression,IsValue);
                        if(owner is not null)valueEdges[owner].Add(reference);
                        break;
                    case BinarySyntax v:stack.Push(v.Right);stack.Push(v.Left);break;
                    case UnarySyntax v:stack.Push(v.Operand);break;
                    case ParenthesizedSyntax v:stack.Push(v.Value);break;
                }
            }
        }

        // Kahn traversal with deterministic ordinal tie-break. Explicit iterative cycle detection:
        // a residual edge is a dependency in a cyclic component or downstream of one.
        private ImmutableArray<BoundDeclaration> Order(Dictionary<string,List<BoundReference>> graph,string code,string message)
        {
            var counts=new Dictionary<string,int>(StringComparer.Ordinal);
            var reverse=graph.Keys.ToDictionary(x=>x,_=>new List<string>(),StringComparer.Ordinal);
            foreach(var (name,edges) in graph)
            {
                cancellation.ThrowIfCancellationRequested();
                var targets=edges.Select(x=>x.Target.Name).Distinct(StringComparer.Ordinal).ToArray();
                counts.Add(name,targets.Length);
                foreach(var target in targets)reverse[target].Add(name);
            }
            var ready=new PriorityQueue<string,string>(StringComparer.Ordinal);
            foreach(var (name,count) in counts)if(count==0)ready.Enqueue(name,name);
            var result=ImmutableArray.CreateBuilder<BoundDeclaration>();
            while(ready.TryDequeue(out var name,out _))
            {
                cancellation.ThrowIfCancellationRequested();
                result.Add(declarations[name]);
                foreach(var dependent in reverse[name])if(--counts[dependent]==0)ready.Enqueue(dependent,dependent);
            }
            if(result.Count!=graph.Count)
            {
                // Follow residual dependencies until an actual cycle is reached, not merely a dependent node.
                var current=counts.Where(p=>p.Value>0).Select(p=>p.Key).Order(StringComparer.Ordinal).First();
                var visited=new HashSet<string>(StringComparer.Ordinal);
                while(visited.Add(current))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var edge=graph[current].First(x=>counts[x.Target.Name]>0);
                    if(visited.Contains(edge.Target.Name))
                        throw new BindFailure(code,message,edge.UseSpan,edge.Target.NameSpan);
                    current=edge.Target.Name;
                }
                throw new InvalidOperationException("Residual dependency graph was inconsistent.");
            }
            return result.ToImmutable();
        }

        // Bounded suggestions: examine at most 512 ordinal candidates with length difference <=2,
        // exact Levenshtein distance <=2, no external vocabulary.
        private ImmutableArray<string> Suggest(string name,Func<DeclarationKind,bool> allowed)
        {
            var matches=new List<(string Name,int Distance)>();
            foreach(var candidate in declarations.Values.Where(x=>allowed(x.Kind)&&Math.Abs(x.Name.Length-name.Length)<=2)
                .OrderBy(x=>x.Name,StringComparer.Ordinal).Take(512))
            {
                cancellation.ThrowIfCancellationRequested();
                int distance=Distance(name,candidate.Name);
                if(distance<=2)matches.Add((candidate.Name,distance));
            }
            return [..matches.OrderBy(x=>x.Distance).ThenBy(x=>x.Name,StringComparer.Ordinal).Take(3).Select(x=>x.Name)];
        }

        private static int Distance(string a,string b)
        {
            Span<int> prior=stackalloc int[b.Length+1];Span<int> next=stackalloc int[b.Length+1];
            for(int j=0;j<=b.Length;j++)prior[j]=j;
            for(int i=1;i<=a.Length;i++)
            {
                next[0]=i;
                for(int j=1;j<=b.Length;j++)next[j]=Math.Min(Math.Min(prior[j]+1,next[j-1]+1),prior[j-1]+(a[i-1]==b[j-1]?0:1));
                var swap=prior;prior=next;next=swap;
            }
            return prior[b.Length];
        }
    }
}
