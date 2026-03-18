using Microsoft.Extensions.Logging;

using TreeSitter;

namespace Aporia.CodeGraph;

public class CSharpParser(ILogger<CSharpParser> logger) : ILanguageParser
{
    public string Language => "csharp";

    public bool CanParse(string filePath) =>
        filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> TypeDeclarations = [
        "class_declaration", "interface_declaration", "struct_declaration",
        "enum_declaration", "record_declaration"
    ];

    private static readonly HashSet<string> MemberDeclarations = [
        "method_declaration", "constructor_declaration", "property_declaration"
    ];

    public (List<SymbolNode> Symbols, List<SymbolReference> References) Parse(string content, string filePath)
    {
        var symbols = new List<SymbolNode>();
        var references = new List<SymbolReference>();
        var lines = content.Split('\n');

        try
        {
            using var lang = new TreeSitter.Language("c-sharp");
            using var parser = new Parser(lang);
            using var tree = parser.Parse(content);
            if (tree is null) return (symbols, references);

            Walk(tree.RootNode, symbols, references, lines, enclosing: null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse {FilePath}", filePath);
        }

        return (symbols, references);
    }

    private static void Walk(Node node, List<SymbolNode> symbols, List<SymbolReference> references, string[] lines, string? enclosing)
    {
        var type = node.Type;

        if (TypeDeclarations.Contains(type))
        {
            var name = FindChildIdentifier(node);
            if (name is not null)
            {
                var kind = type switch
                {
                    "class_declaration" => "class",
                    "interface_declaration" => "interface",
                    "struct_declaration" => "struct",
                    "enum_declaration" => "enum",
                    "record_declaration" => "record",
                    _ => type
                };

                var startLine = node.StartPosition.Row + 1;
                var endLine = node.EndPosition.Row + 1;
                var sig = lines.ElementAtOrDefault(startLine - 1)?.Trim() ?? name;

                symbols.Add(new SymbolNode
                {
                    Name = name,
                    Kind = kind,
                    StartLine = startLine,
                    EndLine = endLine,
                    Signature = sig,
                    Enclosing = enclosing
                });

                // Recurse into the type with this as enclosing
                foreach (var child in node.NamedChildren)
                    Walk(child, symbols, references, lines, name);

                // Extract base types
                foreach (var child in node.NamedChildren)
                {
                    if (child.Type == "base_list")
                    {
                        foreach (var baseType in child.NamedChildren)
                        {
                            var baseName = baseType.Text;
                            var angle = baseName.IndexOf('<');
                            if (angle > 0) baseName = baseName[..angle];

                            references.Add(new SymbolReference
                            {
                                Target = baseName.Trim(),
                                Kind = "implements",
                                Line = baseType.StartPosition.Row + 1
                            });
                        }
                    }
                }

                return; // already recursed with new enclosing
            }
        }

        if (MemberDeclarations.Contains(type))
        {
            var name = FindChildIdentifier(node);
            if (name is not null)
            {
                var kind = type switch
                {
                    "method_declaration" => "method",
                    "constructor_declaration" => "constructor",
                    "property_declaration" => "property",
                    _ => type
                };

                var startLine = node.StartPosition.Row + 1;
                var endLine = node.EndPosition.Row + 1;
                var sig = lines.ElementAtOrDefault(startLine - 1)?.Trim() ?? name;

                symbols.Add(new SymbolNode
                {
                    Name = name,
                    Kind = kind,
                    StartLine = startLine,
                    EndLine = endLine,
                    Signature = sig,
                    Enclosing = enclosing
                });
            }
        }

        if (type == "invocation_expression")
        {
            var funcNode = node.NamedChildren.FirstOrDefault();
            if (funcNode is not null)
            {
                var text = SimplifyTarget(funcNode.Text);
                references.Add(new SymbolReference
                {
                    Target = text,
                    Kind = "calls",
                    Line = node.StartPosition.Row + 1
                });
            }
        }

        if (type == "object_creation_expression")
        {
            // Find the type being created
            var typeNode = node.NamedChildren.FirstOrDefault(c => c.Type != "argument_list");
            if (typeNode is not null)
            {
                var text = SimplifyTarget(typeNode.Text);
                references.Add(new SymbolReference
                {
                    Target = text,
                    Kind = "calls",
                    Line = node.StartPosition.Row + 1
                });
            }
        }

        if (type == "using_directive")
        {
            var target = node.NamedChildren.FirstOrDefault();
            if (target is not null)
            {
                references.Add(new SymbolReference
                {
                    Target = target.Text,
                    Kind = "imports",
                    Line = node.StartPosition.Row + 1
                });
            }
        }

        // Recurse
        foreach (var child in node.NamedChildren)
            Walk(child, symbols, references, lines, enclosing);
    }

    private static string? FindChildIdentifier(Node node)
    {
        foreach (var child in node.NamedChildren)
        {
            if (child.Type == "identifier")
                return child.Text;
        }
        return null;
    }

    private static string SimplifyTarget(string text)
    {
        // Simplify dotted expressions to last two parts
        if (text.Contains('.'))
        {
            var parts = text.Split('.');
            text = parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : parts[^1];
        }

        var angle = text.IndexOf('<');
        if (angle > 0) text = text[..angle];

        var paren = text.IndexOf('(');
        if (paren > 0) text = text[..paren];

        return text.Trim();
    }
}
