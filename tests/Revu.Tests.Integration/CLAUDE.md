# Integration Tests

End-to-end tests that hit real Azure DevOps APIs and real LLMs via `AppFixture`.
Needs API credentials — see root README for secrets setup.

## Rules

- **Never run integration tests automatically** — they call real APIs and cost money.
- Never add integration tests to CI or to a "run all tests" command.

## Conventions

- One test class per production class, named `{Class}Tests.cs`.
- `[Fact]` for single cases, `[Theory]` + `[InlineData]` for parameterized.
- Test names: `Method_Scenario_Expected` (e.g. `Parse_Null_ReturnsDefault`).
- No section-separator comments — test grouping is implicit from method names.
- Fix all nullable warnings — use `!` on nullable properties when the test knows the value is non-null.
- Global `using Xunit;` is in the csproj — no need to import in test files.
- Use NSubstitute for mocks. Prefer `Substitute.For<T>()` over hand-rolled fakes.
- `AppFixture` wires the full DI container (real ADO connector, Cosmos, chat clients).
  Sessions are captured to local JSON files under `sessions/` per run.
