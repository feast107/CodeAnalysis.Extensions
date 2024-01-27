﻿global using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Feast.CodeAnalysis.LiteralGenerator;

[Generator(LanguageNames.CSharp)]
public class LiteralGenerator : IIncrementalGenerator
{
    private const string AttributeName = "System.LiteralAttribute";
    
    private const string LiteralAttribute =
        """
        using System;
        namespace System;
        #nullable enable
        [global::System.AttributeUsage(global::System.AttributeTargets.Class | global::System.AttributeTargets.Struct | global::System.AttributeTargets.Interface)]
        public class LiteralAttribute : Attribute
        {
            public string? FieldName { get; set; }
        
            public LiteralAttribute(string belongToFullyQualifiedClassName){ }
        }
        """;
    
    internal static SyntaxTriviaList Header =
        TriviaList(
            Comment($"// <auto-generated/> By {nameof(Feast)}.{nameof(CodeAnalysis)}"),
            Trivia(PragmaWarningDirectiveTrivia(Token(SyntaxKind.DisableKeyword), true)),
            Trivia(NullableDirectiveTrivia(Token(SyntaxKind.EnableKeyword), true)));

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
        {
            ctx.AddSource($"{nameof(LiteralAttribute)}.g.cs", SourceText.From(LiteralAttribute, Encoding.UTF8));
        });
        
        var provider = context.SyntaxProvider
            .ForAttributeWithMetadataName(AttributeName,
                (ctx, t) => ctx is TypeDeclarationSyntax,
                transform: (ctx, t) => ctx);

        context.RegisterSourceOutput(context.CompilationProvider.Combine(provider.Collect()),
            (ctx, t) =>
            {
                HashSet<string> files = new();
                foreach (var syntax in t.Right)
                {
                    var config = syntax.Attributes.First(x =>
                        x.AttributeClass!.ToDisplayString() == AttributeName);
                    if (config.ConstructorArguments[0].Kind  == TypedConstantKind.Error ||
                        config.ConstructorArguments[0].Value == null) continue;
                    var fullClassName = (config.ConstructorArguments[0].Value as string)!.Split('.');
                    if (fullClassName.Length < 2) continue;
                    var fieldName = config.NamedArguments is [{ Key: "FieldName", Value.Value: string }]
                                    && !string.IsNullOrWhiteSpace(config.NamedArguments[0].Value.Value as string)
                        ? (config.NamedArguments[0].Value.Value as string)!
                        : "Text";

                    var classDeclare = (syntax.TargetNode as TypeDeclarationSyntax)!;
                    var attrList     = new SyntaxList<AttributeListSyntax>();
                    var classSymbol  = (syntax.TargetSymbol as INamedTypeSymbol)!;
                    var attrSymbols  = classSymbol.GetAttributes();
                    var count        = 0;
                    foreach (var (attributeList, index) in classDeclare.AttributeLists.Select((x, i) => (x, i)))
                    {
                        var attrs = new SeparatedSyntaxList<AttributeSyntax>();
                        foreach (var attribute in attributeList.Attributes)
                        {
                            if (attrSymbols[count++].AttributeClass!.ToDisplayString() != AttributeName)
                            {
                                attrs = attrs.Add(attribute);
                            }
                        }

                        if (attrs.Count > 0)
                        {
                            attrList = attrList.Add(AttributeList(attrs));
                        }
                    }

                    (classDeclare.Members.FirstOrDefault(x => x is MethodDeclarationSyntax)
                        as MethodDeclarationSyntax)?.FullQualifiedTypeNameMethod(syntax.SemanticModel);
                    var sourceNamespace =
                        classDeclare.Parent switch
                        {
                            BaseNamespaceDeclarationSyntax namespaceSymbol => namespaceSymbol!,
                            CompilationUnitSyntax compilationUnitSyntax =>
                                (compilationUnitSyntax.Members.First(x => x is BaseNamespaceDeclarationSyntax) as
                                    BaseNamespaceDeclarationSyntax)!,
                        };
                    var newNamespace = sourceNamespace.ReplaceNode(
                        classDeclare,
                        classDeclare.WithAttributeLists(attrList));
                    var file = sourceNamespace.Parent!;
                    file = file.ReplaceNode(sourceNamespace, newNamespace);
                    while (file is BaseNamespaceDeclarationSyntax namespaceSymbol)
                    {
                        file = namespaceSymbol.Parent;
                    }

                    var full =
                        file!
                            .NormalizeWhitespace()
                            .GetText(Encoding.UTF8);
                    var @namespace = string.Join(".", fullClassName.Take(fullClassName.Length - 1));
                    var className  = fullClassName.Last();
                    var content = $"internal const string {fieldName} = \"\"\"\n"
                                  + full
                                  + "\n\"\"\";";
                    var code = CompilationUnit()
                        .AddMembers(
                            NamespaceDeclaration(IdentifierName(@namespace))
                                .WithLeadingTrivia(Header)
                                .AddMembers(
                                    ClassDeclaration(className)
                                        .AddModifiers(Token(SyntaxKind.PartialKeyword))
                                        .AddMembers(ParseMemberDeclaration(content)!)
                                ));
                    var fileName = Path.GetFileNameWithoutExtension(file?.GetLocation().SourceTree?.FilePath ?? string.Empty);
                    ctx.AddSource($"{fileName}.g.cs", code.NormalizeWhitespace().GetText(Encoding.UTF8));
                }
            });
    }
}