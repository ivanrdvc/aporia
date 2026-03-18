using Microsoft.Extensions.Logging;

using TreeSitter;

namespace Aporia.CodeGraph;

public class TypeScriptParser(ILogger<TypeScriptParser> logger) : ILanguageParser
{
    public string Language => "typescript";

    public bool CanParse(string filePath) =>
        filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> SymbolKinds = new()
    {
        ["class_declaration"] = "class",
        ["interface_declaration"] = "interface",
        ["function_declaration"] = "function",
        ["method_definition"] = "method",
        ["method_signature"] = "method",
        ["type_alias_declaration"] = "type",
        ["enum_declaration"] = "enum",
    };

    private static readonly HashSet<string> TypeDeclarations = ["class_declaration", "interface_declaration"];

    private static readonly HashSet<string> NameIdentifiers = [
        "identifier", "type_identifier", "property_identifier"
    ];

    public (List<SymbolNode> Symbols, List<SymbolReference> References) Parse(string content, string filePath)
    {
        var symbols = new List<SymbolNode>();
        var references = new List<SymbolReference>();
        var lines = content.Split('\n');

        try
        {
            var langId = filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? "tsx" : "typescript";
            using var lang = new TreeSitter.Language(langId);
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

        // export_statement wraps the actual declaration — unwrap
        if (type == "export_statement")
        {
            foreach (var child in node.NamedChildren)
                Walk(child, symbols, references, lines, enclosing);
            return;
        }

        if (SymbolKinds.TryGetValue(type, out var kind))
        {
            var name = FindChildName(node);
            if (name is not null)
            {
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

                if (TypeDeclarations.Contains(type))
                {
                    ExtractHeritage(node, references);
                    foreach (var child in node.NamedChildren)
                        Walk(child, symbols, references, lines, name);
                    return;
                }
            }
        }

        // Arrow functions: lexical_declaration → variable_declarator → identifier + arrow_function
        if (type == "lexical_declaration")
        {
            foreach (var declarator in node.NamedChildren.Where(c => c.Type == "variable_declarator"))
            {
                var hasArrow = declarator.NamedChildren.Any(c => c.Type == "arrow_function");
                if (!hasArrow) continue;

                var name = FindChildName(declarator);
                if (name is not null)
                {
                    var startLine = node.StartPosition.Row + 1;
                    var endLine = node.EndPosition.Row + 1;
                    var sig = lines.ElementAtOrDefault(startLine - 1)?.Trim() ?? name;

                    symbols.Add(new SymbolNode
                    {
                        Name = name,
                        Kind = "function",
                        StartLine = startLine,
                        EndLine = endLine,
                        Signature = sig,
                        Enclosing = enclosing
                    });
                }
            }
        }

        // References
        if (type == "call_expression")
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

        if (type == "new_expression")
        {
            var ctorNode = node.NamedChildren.FirstOrDefault(c => c.Type != "arguments");
            if (ctorNode is not null)
            {
                var text = SimplifyTarget(ctorNode.Text);
                references.Add(new SymbolReference
                {
                    Target = text,
                    Kind = "calls",
                    Line = node.StartPosition.Row + 1
                });
            }
        }

        if (type == "import_statement")
        {
            var source = node.NamedChildren.FirstOrDefault(c => c.Type == "string");
            if (source is not null)
            {
                var text = source.Text.Trim('\'', '"');
                references.Add(new SymbolReference
                {
                    Target = text,
                    Kind = "imports",
                    Line = node.StartPosition.Row + 1
                });
            }
        }

        // Recurse
        foreach (var child in node.NamedChildren)
            Walk(child, symbols, references, lines, enclosing);
    }

    private static void ExtractHeritage(Node classNode, List<SymbolReference> references)
    {
        foreach (var child in classNode.NamedChildren)
        {
            if (child.Type is "class_heritage" or "extends_clause" or "implements_clause")
            {
                foreach (var typeNode in child.NamedChildren)
                {
                    if (NameIdentifiers.Contains(typeNode.Type))
                    {
                        references.Add(new SymbolReference
                        {
                            Target = typeNode.Text.Trim(),
                            Kind = "implements",
                            Line = typeNode.StartPosition.Row + 1
                        });
                    }
                    // Recurse into heritage clauses
                    ExtractHeritage(typeNode, references);
                }
            }
        }
    }

    private static string? FindChildName(Node node)
    {
        foreach (var child in node.NamedChildren)
        {
            if (NameIdentifiers.Contains(child.Type))
                return child.Text;
        }
        return null;
    }

    private static string SimplifyTarget(string text)
    {
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
