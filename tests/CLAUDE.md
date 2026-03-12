# Tests

xUnit with `dotnet test`. Each test project has its own CLAUDE.md with specific rules.

## Running Unit Tests

```bash
dotnet test tests/Revu.Tests.Unit/Revu.Tests.Unit.csproj
```

## Shared Conventions

- One test class per production class, named `{Class}Tests.cs`.
- `[Fact]` for single cases, `[Theory]` + `[InlineData]` for parameterized.
- Test names: `Method_Scenario_Expected` (e.g. `Parse_Null_ReturnsDefault`).
- No section-separator comments — test grouping is implicit from method names.
- Fix all nullable warnings — use `!` on nullable properties when the test knows the value is non-null.
- Global `using Xunit;` is in the csproj — no need to import in test files.
- Use NSubstitute for mocks. Prefer `Substitute.For<T>()` over hand-rolled fakes.
