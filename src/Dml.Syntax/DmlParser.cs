using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowDi.Udir;

namespace WowDi.Dml.Syntax;

/// <summary>Bounded, deterministic syntax-only parser. Source text is never executed.</summary>
public static class DmlParser
{
    public const string GrammarVersion = "dml/0.1";
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumTokens = 65_536;
    public const int MaximumExpressionDepth = 64;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumStringLength = 4096;
    public const int MaximumUnitLength = 256;
    public static DmlParseResult Parse(string sourceId, ReadOnlyMemory<byte> utf8, string grammarVersion = GrammarVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        cancellationToken.ThrowIfCancellationRequested();
        if (grammarVersion != GrammarVersion)
            return Reject("DML-VERSION-001", "Unsupported DML grammar version.", new(sourceId,0,0), "");
        if (utf8.Length > MaximumBytes)
            return Reject("DML-LIMIT-001", "Source exceeds the byte limit.", new(sourceId,0,0), "");
        var bytes = utf8.ToArray();
        string text;
        try { text = new UTF8Encoding(false,true).GetString(bytes); }
        catch (DecoderFallbackException)
        { return Reject("DML-LEX-001", "Expected valid UTF-8 source.", new(sourceId,0,0), ""); }
        var source = new SourceDocument(sourceId, Convert.ToHexStringLower(SHA256.HashData(bytes)), text.Length);
        try
        {
            var tokens = new Lexer(sourceId,text,cancellationToken).Read();
            var problem = new Parser(tokens,cancellationToken).Read();
            return new(source,problem,null);
        }
        catch (SyntaxFailure ex)
        { return Reject(ex.Code,ex.Message,ex.Span,text,source); }
    }

    private static DmlParseResult Reject(string code,string message,SourceSpan span,string text,SourceDocument? source=null)
    {
        int line=1,column=1;
        for(int i=0;i<span.Start&&i<text.Length;i++)
        {
            if(text[i]=='\r') { line++; column=1; if(i+1<span.Start&&text[i+1]=='\n')i++; }
            else if(text[i]=='\n') { line++;column=1; }
            else column++;
        }
        return new(source,null,new(code,message,span,line,column));
    }
    private sealed class SyntaxFailure(string code,string message,SourceSpan span) : Exception(message)
    {
        internal string Code {get;}=code;
        internal SourceSpan Span {get;}=span;
    }

    private sealed class Lexer(string source,string text,CancellationToken cancellation)
    {
        private int p;
        private readonly ImmutableArray<Token>.Builder tokens=ImmutableArray.CreateBuilder<Token>();
        private static readonly Dictionary<string,TokenKind> Keywords = new(StringComparer.Ordinal)
        {
            ["problem"]=TokenKind.Problem,["resource"]=TokenKind.Resource,["param"]=TokenKind.Param,
            ["decision"]=TokenKind.Decision,["in"]=TokenKind.In,["constraint"]=TokenKind.Constraint,
            ["minimize"]=TokenKind.Minimize,["maximize"]=TokenKind.Maximize,["priority"]=TokenKind.Priority,
            ["window"]=TokenKind.Window,["from"]=TokenKind.From,["until"]=TokenKind.Until,["calendar"]=TokenKind.Calendar,
            ["source"]=TokenKind.Source,["schema"]=TokenKind.Schema,["on"]=TokenKind.On,["change"]=TokenKind.Change,
            ["reoptimize"]=TokenKind.Reoptimize,["scenario"]=TokenKind.Scenario,["set"]=TokenKind.Set,
            ["depends"]=TokenKind.Depends,["policy"]=TokenKind.Policy,["hard"]=TokenKind.Hard,
            ["soft"]=TokenKind.Soft,["penalty"]=TokenKind.Penalty,["Int"]=TokenKind.Int,["Rational"]=TokenKind.Rational,
            ["Bool"]=TokenKind.Bool,["true"]=TokenKind.True,["false"]=TokenKind.False
        };
        private SourceSpan Span(int start)=>new(source,start,p-start);
        private SyntaxFailure Fail(string code,string message,int start)=>new(code,message,Span(start));
        private void Emit(TokenKind kind,int start,string? value=null)
        {
            if(tokens.Count>=MaximumTokens)throw Fail("DML-LIMIT-002","Token limit exceeded.",start);
            tokens.Add(new(kind,value??text[start..p],Span(start)));
        }
        private bool Take(char c)
        { if(p>=text.Length||text[p]!=c)return false;p++;return true; }
        internal ImmutableArray<Token> Read()
        {
            while(p<text.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int start=p;
                char c=text[p++];
                if(c is ' ' or '\t' or '\r' or '\n')continue;
                if(char.IsAsciiLetter(c))
                {
                    while(p<text.Length&&(char.IsAsciiLetterOrDigit(text[p])||text[p]=='_'))p++;
                    if(p-start>MaximumIdentifierLength)throw Fail("DML-LIMIT-003","Identifier too long.",start);
                    var word=text[start..p];
                    Emit(Keywords.GetValueOrDefault(word,TokenKind.Identifier),start,word);continue;
                }
                if(char.IsAsciiDigit(c))
                {
                    while(p<text.Length&&char.IsAsciiDigit(text[p]))p++;
                    int whole=p-start, fractional=0;
                    if(Take('.'))
                    {
                        int begin=p;
                        while(p<text.Length&&char.IsAsciiDigit(text[p]))p++;
                        fractional=p-begin;
                        if(fractional==0)throw Fail("DML-LEX-002","Expected decimal digits.",start);
                    }
                    if(whole+fractional>256||fractional>255)throw Fail("DML-LIMIT-004","Exact numeric literal exceeds digit limit.",start);
                    Emit(TokenKind.Number,start);continue;
                }
                if(c=='"')
                {
                    bool closed=false;
                    while(p<text.Length)
                    {
                        char ch=text[p++];
                        if(ch=='"'){closed=true;break;}
                        if(ch=='\\'&&p<text.Length)p++;
                        if(p-start>MaximumStringLength*6+2)throw Fail("DML-LIMIT-005","String literal too long.",start);
                    }
                    if(!closed)throw Fail("DML-LEX-003","Unterminated string literal.",start);
                    string decoded;
                    try { decoded=JsonSerializer.Deserialize<string>(text[start..p])!; _=new UTF8Encoding(false,true).GetByteCount(decoded); }
                    catch(Exception ex) when(ex is JsonException or EncoderFallbackException or InvalidOperationException)
                    {throw Fail("DML-LEX-003","Invalid JSON string escape.",start);}
                    if(decoded.Length>MaximumStringLength)throw Fail("DML-LIMIT-005","String literal too long.",start);
                    Emit(TokenKind.String,start,decoded);continue;
                }
                var kind=c switch
                {
                    '{'=>TokenKind.OpenBrace,'}'=>TokenKind.CloseBrace,'['=>TokenKind.OpenBracket,']'=>TokenKind.CloseBracket,
                    '('=>TokenKind.OpenParen,')'=>TokenKind.CloseParen,':'=>TokenKind.Colon,';'=>TokenKind.Semicolon,
                    ','=>TokenKind.Comma,'+'=>TokenKind.Plus,'-'=>TokenKind.Minus,'*'=>TokenKind.Star,'/'=>TokenKind.Slash,
                    '='=>Take('=')?TokenKind.Equal:TokenKind.Assign,
                    '<' when Take('=')=>TokenKind.LessEqual,'>' when Take('=')=>TokenKind.GreaterEqual,
                    _=>throw Fail("DML-LEX-004","Unexpected character.",start)
                };
                Emit(kind,start);
            }
            tokens.Add(new(TokenKind.End,"",new(source,p,0)));
            return tokens.ToImmutable();
        }
    }

    private sealed class Parser(ImmutableArray<Token> tokens,CancellationToken cancellation)
    {
        private int pos;
        private Token Current=>tokens[pos];
        private Token Prior=>tokens[pos-1];
        private Token Next(){cancellation.ThrowIfCancellationRequested();return tokens[pos++];}
        private bool At(TokenKind kind)=>Current.Kind==kind;
        private bool Take(TokenKind kind){if(!At(kind))return false;Next();return true;}
        private Token Expect(TokenKind kind)=>At(kind)?Next():throw Error("DML-SYN-001","Expected "+kind+".",Current.Span);
        private static SyntaxFailure Error(string code,string message,SourceSpan span)=>new(code,message,span);
        private static SourceSpan Cover(SourceSpan first,SourceSpan last)=>new(first.SourceId,first.Start,last.Start+last.Length-first.Start);
        private SourceSpan Since(Token start)=>Cover(start.Span,Prior.Span);
        private Token Name()=>Expect(TokenKind.Identifier);
        internal ProblemSyntax Read()
        {
            var start=Expect(TokenKind.Problem);var name=Name();Expect(TokenKind.OpenBrace);
            var declarations=ImmutableArray.CreateBuilder<DeclarationSyntax>();
            while(!At(TokenKind.CloseBrace))declarations.Add(Declaration());
            Expect(TokenKind.CloseBrace);var span=Since(start);Expect(TokenKind.End);
            return new(name,declarations.ToImmutable(),span);
        }
        private DeclarationSyntax Declaration()
        {
            var start=Current;
            switch(start.Kind)
            {
                case TokenKind.Resource:case TokenKind.Param:
                {
                    Next();var name=Name();Expect(TokenKind.Colon);var type=Type();Expect(TokenKind.Assign);
                    var value=Expr();Expect(TokenKind.Semicolon);return new ValueDeclaration(start,name,type,value,Since(start));
                }
                case TokenKind.Decision:
                {
                    Next();var name=Name();Expect(TokenKind.Colon);var type=Type();
                    ExpressionSyntax? lo=null,hi=null;
                    if(type.Kind.Kind!=TokenKind.Bool||At(TokenKind.In))
                    {Expect(TokenKind.In);Expect(TokenKind.OpenBracket);lo=Expr();Expect(TokenKind.Comma);hi=Expr();Expect(TokenKind.CloseBracket);}
                    Expect(TokenKind.Semicolon);return new DecisionDeclaration(name,type,lo,hi,Since(start));
                }
                case TokenKind.Constraint:
                {
                    Next();var name=Name();Expect(TokenKind.Colon);var left=Expr();
                    if(Current.Kind is not(TokenKind.Equal or TokenKind.LessEqual or TokenKind.GreaterEqual))
                        throw Error("DML-SYN-002","Expected a comparison relation.",Current.Span);
                    var rel=Next();var right=Expr();Token? penalty=null;
                    if(Take(TokenKind.Soft)){Expect(TokenKind.Penalty);penalty=Expect(TokenKind.Number);}
                    Expect(TokenKind.Semicolon);return new ConstraintDeclaration(name,left,rel,right,penalty,Since(start));
                }
                case TokenKind.Minimize:case TokenKind.Maximize:
                {
                    Next();var name=Name();Expect(TokenKind.Priority);var priority=Expect(TokenKind.Number);
                    if(priority.Text.Contains('.'))throw Error("DML-SYN-003","Expected an unsigned integer priority.",priority.Span);
                    Expect(TokenKind.Colon);var value=Expr();Expect(TokenKind.Semicolon);return new ObjectiveDeclaration(start,name,priority,value,Since(start));
                }
                case TokenKind.Window:
                {
                    Next();var name=Name();Expect(TokenKind.From);var from=Expect(TokenKind.String);
                    Expect(TokenKind.Until);var until=Expect(TokenKind.String);Expect(TokenKind.Calendar);var calendar=Name();
                    Expect(TokenKind.Semicolon);return new WindowDeclaration(name,from,until,calendar,Since(start));
                }
                case TokenKind.Source:
                {Next();var name=Name();Expect(TokenKind.Schema);var schema=Expect(TokenKind.String);Expect(TokenKind.Semicolon);return new SourceDeclaration(name,schema,Since(start));}
                case TokenKind.On:
                {Next();Expect(TokenKind.Change);var name=Name();Expect(TokenKind.Reoptimize);Expect(TokenKind.Semicolon);return new TriggerDeclaration(name,Since(start));}
                case TokenKind.Scenario:
                {
                    Next();var name=Name();Expect(TokenKind.OpenBrace);var overrides=ImmutableArray.CreateBuilder<OverrideSyntax>();
                    while(!At(TokenKind.CloseBrace)){var set=Expect(TokenKind.Set);var target=Name();Expect(TokenKind.Assign);var value=Expr();Expect(TokenKind.Semicolon);overrides.Add(new(target,value,Since(set)));}
                    Expect(TokenKind.CloseBrace);return new ScenarioDeclaration(name,overrides.ToImmutable(),Since(start));
                }
                case TokenKind.Depends:
                {Next();var name=Name();Expect(TokenKind.On);var target=Name();Expect(TokenKind.Semicolon);return new DependencyDeclaration(name,target,Since(start));}
                case TokenKind.Policy:
                {Next();var name=Name();Expect(TokenKind.Hard);var target=Name();Expect(TokenKind.Semicolon);return new PolicyDeclaration(name,target,Since(start));}
                default:throw Error("DML-SYN-004","Expected a declaration.",Current.Span);
            }
        }
        private TypeSyntax Type()
        {
            if(Current.Kind is not(TokenKind.Int or TokenKind.Rational or TokenKind.Bool))
                throw Error("DML-SYN-005","Expected a scalar type.",Current.Span);
            var start=Next();var unit=start.Kind!=TokenKind.Bool&&At(TokenKind.OpenBracket)?Unit():null;
            return new(start,unit,Since(start));
        }
        private UnitSyntax Unit()
        {
            var start=Expect(TokenKind.OpenBracket);var parts=ImmutableArray.CreateBuilder<Token>();parts.Add(Name());
            while(Current.Kind is TokenKind.Star or TokenKind.Slash){parts.Add(Next());parts.Add(Name());}
            Expect(TokenKind.CloseBracket);
            if(parts.Sum(x=>x.Text.Length)>MaximumUnitLength)throw Error("DML-LIMIT-006","Unit expression too long.",Since(start));
            return new(parts.ToImmutable(),Since(start));
        }
        // Pratt parser with a bounded recursion stack AND bounded generated tree height.
        private (ExpressionSyntax Node,int Height) ParseExpression(int min=0,int depth=1)
        {
            if(depth>MaximumExpressionDepth)throw Error("DML-LIMIT-007","Expression nesting limit exceeded.",Current.Span);
            var start=Current;(ExpressionSyntax Node,int Height) left;
            if(Take(TokenKind.Minus))
            {
                var operand=Primary(depth+1);
                if(operand.Height>=MaximumExpressionDepth)throw Error("DML-LIMIT-007","Expression nesting limit exceeded.",start.Span);
                left=(new UnarySyntax(start,operand.Node,Since(start)),operand.Height+1);
            }
            else left=Primary(depth);
            while(true)
            {
                int precedence=Current.Kind switch{TokenKind.Plus or TokenKind.Minus=>1,TokenKind.Star or TokenKind.Slash=>2,_=>0};
                if(precedence==0||precedence<min)break;
                var op=Next();var right=ParseExpression(precedence+1,depth+1);
                int height=Math.Max(left.Height,right.Height)+1;
                if(height>MaximumExpressionDepth)throw Error("DML-LIMIT-007","Expression nesting limit exceeded.",op.Span);
                left=(new BinarySyntax(left.Node,op,right.Node,Cover(left.Node.Span,right.Node.Span)),height);
            }
            return left;
        }
        private (ExpressionSyntax Node,int Height) Primary(int depth)
        {
            if(depth>MaximumExpressionDepth)throw Error("DML-LIMIT-007","Expression nesting limit exceeded.",Current.Span);
            var start=Current;
            if(Take(TokenKind.Number)){var unit=At(TokenKind.OpenBracket)?Unit():null;return(new NumberSyntax(start,unit,Since(start)),1);}
            if(Take(TokenKind.True)||Take(TokenKind.False))return(new BooleanSyntax(start,start.Span),1);
            if(Take(TokenKind.Identifier))return(new ReferenceSyntax(start,start.Span),1);
            if(Take(TokenKind.OpenParen))
            {
                var expr=ParseExpression(0,depth+1);Expect(TokenKind.CloseParen);
                if(expr.Height>=MaximumExpressionDepth)throw Error("DML-LIMIT-007","Expression nesting limit exceeded.",Since(start));
                return(new ParenthesizedSyntax(expr.Node,Since(start)),expr.Height+1);
            }
            throw Error("DML-SYN-006","Expected a literal, reference or parenthesized expression.",Current.Span);
        }
        private ExpressionSyntax Expr()=>ParseExpression().Node;
    }
}
