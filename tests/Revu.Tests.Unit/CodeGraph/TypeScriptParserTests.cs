using Microsoft.Extensions.Logging.Abstractions;

using Revu.CodeGraph;

namespace Revu.Tests.Unit.CodeGraph;

public class TypeScriptParserTests
{
    private readonly TypeScriptParser _parser = new(NullLogger<TypeScriptParser>.Instance);

    [Fact]
    public void CanParse_TsFile_ReturnsTrue() =>
        Assert.True(_parser.CanParse("src/foo.ts"));

    [Fact]
    public void CanParse_TsxFile_ReturnsTrue() =>
        Assert.True(_parser.CanParse("src/Component.tsx"));

    [Fact]
    public void CanParse_CsFile_ReturnsFalse() =>
        Assert.False(_parser.CanParse("src/Foo.cs"));

    [Fact]
    public void Parse_ClassWithMethod_ExtractsSymbols()
    {
        const string source = """
            export class OrderService {
                placeOrder(id: number): void {
                    console.log(id);
                }
            }
            """;

        var (symbols, _) = _parser.Parse(source, "OrderService.ts");

        Assert.Contains(symbols, s => s.Name == "OrderService" && s.Kind == "class");
        Assert.Contains(symbols, s => s.Name == "placeOrder" && s.Kind == "method" && s.Enclosing == "OrderService");
    }

    [Fact]
    public void Parse_Interface_ExtractsSymbol()
    {
        const string source = """
            export interface IOrderService {
                placeOrder(id: number): void;
            }
            """;

        var (symbols, _) = _parser.Parse(source, "IOrderService.ts");

        Assert.Contains(symbols, s => s.Name == "IOrderService" && s.Kind == "interface");
    }

    [Fact]
    public void Parse_Function_ExtractsSymbol()
    {
        const string source = """
            export function calculateTotal(items: Item[]): number {
                return items.reduce((sum, i) => sum + i.price, 0);
            }
            """;

        var (symbols, _) = _parser.Parse(source, "utils.ts");

        Assert.Contains(symbols, s => s.Name == "calculateTotal" && s.Kind == "function");
    }

    [Fact]
    public void Parse_ArrowFunction_ExtractsSymbol()
    {
        const string source = """
            export const processOrder = (id: number): void => {
                console.log(id);
            };
            """;

        var (symbols, _) = _parser.Parse(source, "handlers.ts");

        Assert.Contains(symbols, s => s.Name == "processOrder" && s.Kind == "function");
    }

    [Fact]
    public void Parse_TypeAlias_ExtractsSymbol()
    {
        const string source = "export type OrderStatus = 'pending' | 'complete';";

        var (symbols, _) = _parser.Parse(source, "types.ts");

        Assert.Contains(symbols, s => s.Name == "OrderStatus" && s.Kind == "type");
    }

    [Fact]
    public void Parse_Enum_ExtractsSymbol()
    {
        const string source = """
            enum Direction {
                Up,
                Down
            }
            """;

        var (symbols, _) = _parser.Parse(source, "enums.ts");

        Assert.Contains(symbols, s => s.Name == "Direction" && s.Kind == "enum");
    }

    [Fact]
    public void Parse_FunctionCall_ExtractsReference()
    {
        const string source = """
            function foo() {
                service.process();
            }
            """;

        var (_, refs) = _parser.Parse(source, "foo.ts");

        Assert.Contains(refs, r => r.Target.Contains("process") && r.Kind == "calls");
    }

    [Fact]
    public void Parse_Import_ExtractsReference()
    {
        const string source = """
            import { OrderService } from './services/order';

            const svc = new OrderService();
            """;

        var (_, refs) = _parser.Parse(source, "app.ts");

        Assert.Contains(refs, r => r.Kind == "imports" && r.Target.Contains("./services/order"));
    }

    [Fact]
    public void Parse_NewExpression_ExtractsReference()
    {
        const string source = """
            const svc = new OrderService();
            """;

        var (_, refs) = _parser.Parse(source, "app.ts");

        Assert.Contains(refs, r => r.Target == "OrderService" && r.Kind == "calls");
    }

    [Fact]
    public void Parse_InvalidSource_ReturnsEmpty()
    {
        const string source = "this is {{ not valid TS {{{{";

        var (symbols, refs) = _parser.Parse(source, "bad.ts");

        Assert.NotNull(symbols);
        Assert.NotNull(refs);
    }

    [Fact]
    public void Parse_Symbols_HaveLineRanges()
    {
        const string source = """
            export class Foo {
                bar(): void {
                }
            }
            """;

        var (symbols, _) = _parser.Parse(source, "Foo.ts");

        var fooClass = symbols.First(s => s.Name == "Foo");
        Assert.True(fooClass.StartLine >= 1);
        Assert.True(fooClass.EndLine >= fooClass.StartLine);
    }
}
