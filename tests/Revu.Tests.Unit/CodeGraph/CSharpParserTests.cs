using Microsoft.Extensions.Logging.Abstractions;

using Revu.CodeGraph;

namespace Revu.Tests.Unit.CodeGraph;

public class CSharpParserTests
{
    private readonly CSharpParser _parser = new(NullLogger<CSharpParser>.Instance);

    [Fact]
    public void CanParse_CsFile_ReturnsTrue() =>
        Assert.True(_parser.CanParse("src/Foo.cs"));

    [Fact]
    public void CanParse_TsFile_ReturnsFalse() =>
        Assert.False(_parser.CanParse("src/foo.ts"));

    [Fact]
    public void Parse_ClassWithMethod_ExtractsSymbols()
    {
        const string source = """
            namespace App;

            public class OrderService
            {
                public void PlaceOrder(int id)
                {
                    Console.WriteLine(id);
                }

                public string Name { get; set; }
            }
            """;

        var (symbols, _) = _parser.Parse(source, "OrderService.cs");

        Assert.Contains(symbols, s => s.Name == "OrderService" && s.Kind == "class");
        Assert.Contains(symbols, s => s.Name == "PlaceOrder" && s.Kind == "method" && s.Enclosing == "OrderService");
        Assert.Contains(symbols, s => s.Name == "Name" && s.Kind == "property");
    }

    [Fact]
    public void Parse_Interface_ExtractsSymbol()
    {
        const string source = """
            public interface IOrderService
            {
                void PlaceOrder(int id);
            }
            """;

        var (symbols, _) = _parser.Parse(source, "IOrderService.cs");

        Assert.Contains(symbols, s => s.Name == "IOrderService" && s.Kind == "interface");
        Assert.Contains(symbols, s => s.Name == "PlaceOrder" && s.Kind == "method");
    }

    [Fact]
    public void Parse_Record_ExtractsSymbol()
    {
        const string source = "public record OrderDto(int Id, string Name);";

        var (symbols, _) = _parser.Parse(source, "OrderDto.cs");

        Assert.Contains(symbols, s => s.Name == "OrderDto" && s.Kind == "record");
    }

    [Fact]
    public void Parse_Enum_ExtractsSymbol()
    {
        const string source = """
            public enum Status
            {
                Active,
                Inactive
            }
            """;

        var (symbols, _) = _parser.Parse(source, "Status.cs");

        Assert.Contains(symbols, s => s.Name == "Status" && s.Kind == "enum");
    }

    [Fact]
    public void Parse_MethodCall_ExtractsReference()
    {
        const string source = """
            public class Foo
            {
                public void Bar()
                {
                    service.Process();
                }
            }
            """;

        var (_, refs) = _parser.Parse(source, "Foo.cs");

        Assert.Contains(refs, r => r.Target.Contains("Process") && r.Kind == "calls");
    }

    [Fact]
    public void Parse_ObjectCreation_ExtractsReference()
    {
        const string source = """
            public class Foo
            {
                public void Bar()
                {
                    var x = new OrderService();
                }
            }
            """;

        var (_, refs) = _parser.Parse(source, "Foo.cs");

        Assert.Contains(refs, r => r.Target == "OrderService" && r.Kind == "calls");
    }

    [Fact]
    public void Parse_BaseClass_ExtractsImplementsReference()
    {
        const string source = """
            public class AdoConnector : IGitConnector
            {
            }
            """;

        var (_, refs) = _parser.Parse(source, "AdoConnector.cs");

        Assert.Contains(refs, r => r.Target == "IGitConnector" && r.Kind == "implements");
    }

    [Fact]
    public void Parse_UsingDirective_ExtractsImport()
    {
        const string source = """
            using System.Text;

            public class Foo { }
            """;

        var (_, refs) = _parser.Parse(source, "Foo.cs");

        Assert.Contains(refs, r => r.Kind == "imports");
    }

    [Fact]
    public void Parse_InvalidSource_ReturnsEmpty()
    {
        const string source = "this is not valid C# {{{{";

        var (symbols, refs) = _parser.Parse(source, "Bad.cs");

        // Should not throw — returns whatever tree-sitter could parse
        Assert.NotNull(symbols);
        Assert.NotNull(refs);
    }

    [Fact]
    public void Parse_Symbols_HaveLineRanges()
    {
        const string source = """
            public class Foo
            {
                public void Bar()
                {
                }
            }
            """;

        var (symbols, _) = _parser.Parse(source, "Foo.cs");

        var fooClass = symbols.First(s => s.Name == "Foo");
        Assert.True(fooClass.StartLine >= 1);
        Assert.True(fooClass.EndLine >= fooClass.StartLine);

        var barMethod = symbols.First(s => s.Name == "Bar");
        Assert.True(barMethod.StartLine > fooClass.StartLine);
        Assert.True(barMethod.EndLine <= fooClass.EndLine);
    }

    [Fact]
    public void Parse_Symbols_HaveSignatures()
    {
        const string source = """
            public class OrderService
            {
                public void PlaceOrder(int id) { }
            }
            """;

        var (symbols, _) = _parser.Parse(source, "OrderService.cs");

        var method = symbols.First(s => s.Name == "PlaceOrder");
        Assert.Contains("PlaceOrder", method.Signature);
        Assert.Contains("int id", method.Signature);
    }
}
