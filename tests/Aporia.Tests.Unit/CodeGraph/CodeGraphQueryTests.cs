using Aporia.CodeGraph;

namespace Aporia.Tests.Unit.CodeGraph;

public class CodeGraphQueryTests
{
    [Fact]
    public void Execute_UnknownKind_ReturnsError()
    {
        var sut = new CodeGraphQuery([]);
        var result = sut.Execute("bogus", "Foo");
        Assert.Contains("Unknown query kind", result);
    }

    [Fact]
    public void Execute_NoResults_ReturnsFallback()
    {
        var sut = new CodeGraphQuery([]);
        var result = sut.Execute("outline", "src/Missing.cs");
        Assert.Contains("File not found", result);
    }

    [Fact]
    public void Callers_FindsCallSites()
    {
        var graph = new List<FileIndex>
        {
            File("src/Service.cs", [Symbol("DoWork", "method", 10, 20, enclosing: "Service")],
                [Ref("Repository.Save", "calls", 15)])
        };

        var result = new CodeGraphQuery(graph).Execute("callers", "Save");
        Assert.Contains("Service.cs", result);
        Assert.Contains("DoWork", result);
    }

    [Fact]
    public void Implementations_FindsImplementors()
    {
        var graph = new List<FileIndex>
        {
            File("src/AdoConnector.cs",
                [Symbol("AdoConnector", "class", 1, 50, "class AdoConnector : IGitConnector")],
                [Ref("IGitConnector", "implements", 1)])
        };

        var result = new CodeGraphQuery(graph).Execute("implementations", "IGitConnector");
        Assert.Contains("AdoConnector", result);
    }

    [Fact]
    public void Dependents_FindsReferencingFiles()
    {
        var target = File("src/Models.cs", [Symbol("Order", "class", 1, 10)]);
        var consumer = File("src/Service.cs", refs: [Ref("Order", "calls", 5)]);

        var result = new CodeGraphQuery([target, consumer]).Execute("dependents", "src/Models.cs");
        Assert.Contains("Service.cs", result);
    }

    [Fact]
    public void Dependents_UnknownFile_ReturnsNotFound()
    {
        var result = new CodeGraphQuery([File("src/Foo.cs")]).Execute("dependents", "src/Missing.cs");
        Assert.Contains("File not found", result);
    }

    [Fact]
    public void Outline_ListsSymbols()
    {
        var file = File("src/Foo.cs",
        [
            Symbol("Foo", "class", 1, 30, "public class Foo"),
            Symbol("Bar", "method", 5, 10, "public void Bar()", "Foo")
        ]);

        var result = new CodeGraphQuery([file]).Execute("outline", "src/Foo.cs");
        Assert.Contains("class", result);
        Assert.Contains("Foo", result);
        Assert.Contains("Bar", result);
    }

    [Fact]
    public void Hierarchy_ShowsExtendsAndExtendedBy()
    {
        var iface = File("src/IFoo.cs",
            [Symbol("IFoo", "interface", 1, 5, "interface IFoo")]);
        var impl = File("src/FooImpl.cs",
            [Symbol("FooImpl", "class", 1, 20, "class FooImpl : IFoo")],
            [Ref("IFoo", "implements", 1)]);

        var graph = new CodeGraphQuery([iface, impl]);

        var result = graph.Execute("hierarchy", "IFoo");
        Assert.Contains("extended by", result);
        Assert.Contains("FooImpl", result);

        var implResult = graph.Execute("hierarchy", "FooImpl");
        Assert.Contains("extends: IFoo", implResult);
    }

    private static FileIndex File(string path, List<SymbolNode>? symbols = null, List<SymbolReference>? refs = null) =>
        new()
        {
            Id = path.Replace('/', '|'),
            RepoId = "repo",
            Branch = "refs/heads/main",
            Language = "csharp",
            ContentHash = "abc",
            Symbols = symbols ?? [],
            References = refs ?? []
        };

    private static SymbolNode Symbol(string name, string kind, int start, int end, string? sig = null, string? enclosing = null) =>
        new() { Name = name, Kind = kind, StartLine = start, EndLine = end, Signature = sig ?? $"{kind} {name}", Enclosing = enclosing };

    private static SymbolReference Ref(string target, string kind, int? line = null) =>
        new() { Target = target, Kind = kind, Line = line };
}
