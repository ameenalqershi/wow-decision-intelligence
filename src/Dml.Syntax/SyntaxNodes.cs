using System.Collections.Immutable;
using WowDi.Udir;

namespace WowDi.Dml.Syntax;

public enum TokenKind
{
    End, Identifier, Number, String,
    Problem, Resource, Param, Decision, In, Constraint, Minimize, Maximize, Priority,
    Window, From, Until, Calendar, Source, Schema, On, Change, Reoptimize, Scenario, Set,
    Depends, Policy, Hard, Soft, Penalty, Int, Rational, Bool, True, False,
    OpenBrace, CloseBrace, OpenBracket, CloseBracket, OpenParen, CloseParen,
    Colon, Semicolon, Comma, Assign, Plus, Minus, Star, Slash, Equal, LessEqual, GreaterEqual
}

public sealed record Token(TokenKind Kind, string Text, SourceSpan Span);
public abstract record SyntaxNode(SourceSpan Span);
public sealed record ProblemSyntax(Token Name, ImmutableArray<DeclarationSyntax> Declarations, SourceSpan Span) : SyntaxNode(Span);
public abstract record DeclarationSyntax(SourceSpan Span) : SyntaxNode(Span);
public sealed record UnitSyntax(ImmutableArray<Token> Parts, SourceSpan Span) : SyntaxNode(Span);
public sealed record TypeSyntax(Token Kind, UnitSyntax? Unit, SourceSpan Span) : SyntaxNode(Span);
public sealed record ValueDeclaration(Token Role, Token Name, TypeSyntax Type, ExpressionSyntax Value, SourceSpan Span) : DeclarationSyntax(Span);
public sealed record DecisionDeclaration(Token Name, TypeSyntax Type, ExpressionSyntax? Lower, ExpressionSyntax? Upper, SourceSpan Span) : DeclarationSyntax(Span);
public sealed record ConstraintDeclaration(Token Name, ExpressionSyntax Left, Token Relation, ExpressionSyntax Right,
    Token? Penalty, SourceSpan Span) : DeclarationSyntax(Span);
public sealed record ObjectiveDeclaration(Token Direction, Token Name, Token Priority, ExpressionSyntax Value, SourceSpan Span) : DeclarationSyntax(Span);
public sealed record WindowDeclaration(Token Name, Token From, Token Until, Token Calendar, SourceSpan Span) : DeclarationSyntax(Span);
public sealed record SourceDeclaration(Token Name, Token Schema, SourceSpan Span) : DeclarationSyntax(Span);
public sealed record TriggerDeclaration(Token Source, SourceSpan Span) : DeclarationSyntax(Span);
public sealed record OverrideSyntax(Token Name, ExpressionSyntax Value, SourceSpan Span) : SyntaxNode(Span);
public sealed record ScenarioDeclaration(Token Name, ImmutableArray<OverrideSyntax> Overrides, SourceSpan Span) : DeclarationSyntax(Span);
public sealed record DependencyDeclaration(Token Name, Token DependsOn, SourceSpan Span) : DeclarationSyntax(Span);
public sealed record PolicyDeclaration(Token Name, Token Constraint, SourceSpan Span) : DeclarationSyntax(Span);
public abstract record ExpressionSyntax(SourceSpan Span) : SyntaxNode(Span);
public sealed record NumberSyntax(Token Number, UnitSyntax? Unit, SourceSpan Span) : ExpressionSyntax(Span);
public sealed record BooleanSyntax(Token Value, SourceSpan Span) : ExpressionSyntax(Span);
public sealed record ReferenceSyntax(Token Name, SourceSpan Span) : ExpressionSyntax(Span);
public sealed record UnarySyntax(Token Operator, ExpressionSyntax Operand, SourceSpan Span) : ExpressionSyntax(Span);
public sealed record BinarySyntax(ExpressionSyntax Left, Token Operator, ExpressionSyntax Right, SourceSpan Span) : ExpressionSyntax(Span);
public sealed record ParenthesizedSyntax(ExpressionSyntax Value, SourceSpan Span) : ExpressionSyntax(Span);

public sealed record DmlDiagnostic(string Code, string Message, SourceSpan Span, int Line, int Column);
public sealed record DmlParseResult(SourceDocument? Source, ProblemSyntax? Problem, DmlDiagnostic? Diagnostic)
{
    public bool Accepted => Problem is not null && Diagnostic is null;
}
