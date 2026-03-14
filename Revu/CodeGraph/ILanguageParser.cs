namespace Revu.CodeGraph;

public interface ILanguageParser
{
    string Language { get; }
    bool CanParse(string filePath);
    (List<SymbolNode> Symbols, List<SymbolReference> References) Parse(string content, string filePath);
}
