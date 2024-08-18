﻿global using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using System.Linq;
using System.Text;
using Feast.CodeAnalysis.LiteralGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Feast.CodeAnalysis.Generators.LiteralGenerator;


[Generator(LanguageNames.CSharp)]
public class LiteralGenerator : IIncrementalGenerator
{
    private const string AttributeName = "System.LiteralAttribute";

    private const string LiteralAttribute =
        """
        #nullable enable
        using System;
        namespace System
        {
            /// <summary>
            /// Auto generate full-qualified class for target type
            /// </summary>
            [global::System.AttributeUsage(global::System.AttributeTargets.Class | global::System.AttributeTargets.Struct | global::System.AttributeTargets.Interface | global::System.AttributeTargets.Enum | global::System.AttributeTargets.Delegate)]
            public class LiteralAttribute : Attribute
            {
                /// <summary>
                /// Generated field name in target class, can use template [Namespace], [Class] & [FullName], will be "Text" if null
                /// </summary>
                public string? FieldName { get; set; } = "Text";
            
                /// <summary>
                /// Auto generate full-qualified class for target type
                /// </summary>
                /// <param name="belongToFullyQualifiedClassName">namespace.class style string</param>
                public LiteralAttribute(string belongToFullyQualifiedClassName){ }
            }
        }
        """;

    
    internal static SyntaxTriviaList Header =
        TriviaList(
            Comment($"// <auto-generated/> By {nameof(Feast)}.{nameof(CodeAnalysis)}"),
            Trivia(PragmaWarningDirectiveTrivia(Token(SyntaxKind.DisableKeyword), true)),
            Trivia(NullableDirectiveTrivia(Token(SyntaxKind.EnableKeyword), true)));

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource($"{nameof(LiteralAttribute)}.g.cs",
                          SourceText.From(LiteralAttribute, Encoding.UTF8));
        });

        var provider = context.SyntaxProvider
            .ForAttributeWithMetadataName(AttributeName,
                                          (ctx, t) => ctx is MemberDeclarationSyntax,
                                          transform: (ctx, t) => ctx);

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(provider.Collect()),
            (ctx, t) =>
            {
                foreach (var group in t.Right
                             .Select(static x => (Context: x,
                                         Attribute: x.Attributes.First(
                                             x => x.AttributeClass!.ToDisplayString() ==
                                                  AttributeName)))
                             .Where(static syntax =>
                                        syntax.Attribute.ConstructorArguments[0] is
                                        {
                                            Kind: not TypedConstantKind.Error, Value: not null
                                        })
                             .GroupBy(static x =>
                                          (x.Attribute.ConstructorArguments[0]
                                              .Value as string)!))

                {
                    var splits     = group.Key.Split('.');
                    var @namespace = string.Join(".", splits.Take(splits.Length - 1));
                    var @class     = splits.Last();
                    var classDeclaration = ClassDeclaration(@class)
                        .AddModifiers(Token(SyntaxKind.PartialKeyword)).AddMembers(
                            group.Select(x =>
                            {
                                var syntax = x.Context;
                                var config = x.Attribute;
                                var fieldName = config.NamedArguments is
                                                    [{ Key: "FieldName", Value.Value: string }]
                                                && !string.IsNullOrWhiteSpace(
                                                    config.NamedArguments[0].Value
                                                        .Value as string)
                                    ? (config.NamedArguments[0].Value.Value as string)!
                                    : "Text";

                                var typeDeclaration =
                                    (syntax.TargetNode as MemberDeclarationSyntax)!;
                                var attrList    = new SyntaxList<AttributeListSyntax>();
                                var classSymbol = (syntax.TargetSymbol as INamedTypeSymbol)!;
                                var attrSymbols = classSymbol.GetAttributes();
                                foreach (var (attributeList, index) in typeDeclaration
                                             .AttributeLists.Select(
                                                 (x, i) => (x, i)))
                                {
                                    var attrs = new SeparatedSyntaxList<AttributeSyntax>();
                                    attrs = attributeList.Attributes
                                        .Where(_ => attrSymbols[index].AttributeClass!
                                                        .ToDisplayString() !=
                                                    AttributeName)
                                        .Aggregate(
                                            attrs,
                                            (current, attribute) => current.Add(attribute));

                                    if (attrs.Count > 0)
                                    {
                                        attrList = attrList.Add(AttributeList(attrs));
                                    }
                                }

                                var full = typeDeclaration
                                    .FullQualifiedMember(syntax.SemanticModel)
                                    .WithAttributeLists(attrList)
                                    .FullNamespace(classSymbol)
                                    .WithUsing(typeDeclaration.SyntaxTree
                                                   .GetCompilationUnitRoot())
                                    .NormalizeWhitespace()
                                    .GetText(Encoding.UTF8);
                                var sp = (syntax.TargetSymbol as ITypeSymbol)!
                                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                    .Replace("{", "_")
                                    .Replace("}", "_")
                                    .Replace("<", "_")
                                    .Replace(">", "_")
                                    .Replace("global::", "").Split('.');
                                var fqn = string.Join("_", sp.Take(sp.Length - 1));
                                var fqc = sp.Last();
                                var content = $"internal static string {
                                    (
                                        fieldName.Contains('[') && fieldName.Contains(']')
                                            ?
                                            // is template
                                            fieldName.Replace("[Namespace]", fqn)
                                                .Replace("[Class]", fqc)
                                                .Replace("[FullName]", $"{fqn}_{fqc}")
                                            : fieldName
                                    )
                                } = \"\"\"\n"
                                              + full.ToString().Replace("\"\"\"", "\"^\"\"")
                                              + "\n\"\"\""
                                              + ".Replace(\"\\\"^\\\"\\\"\",\"\\\"\\\"\\\"\");";
                                return ParseMemberDeclaration(content)!;
                            }).ToArray());
                    var text = CompilationUnit()
                        .AddMembers(
                            NamespaceDeclaration(IdentifierName(@namespace))
                                .WithLeadingTrivia(Header)
                                .AddMembers(classDeclaration))
                        .NormalizeWhitespace();
                    ctx.AddSource($"{group.Key}.g.cs", text.GetText(Encoding.UTF8));
                }
            });
    }
}