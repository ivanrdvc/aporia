# Async Patterns

## Missing `ConfigureAwait(false)`
Normal in ASP.NET Core, Azure Functions, and console apps — no SynchronizationContext exists.
Bug in shared libraries (NuGet packages) consumed by WPF/WinForms/legacy ASP.NET.
→ Check: is this app code or a shared library?

## Missing `CancellationToken` propagation
Normal when the operation must complete regardless (audit logs, transactions, completion signals).
Bug when a long-running call (HTTP, DB, file I/O) ignores a token the caller expects to cancel.
→ Check: does the method accept a token but never pass it to awaited calls?

## `async void`
Normal for event handlers where the delegate signature requires void.
Bug everywhere else — caller can't observe exceptions or await completion.
→ Check: is the method an event handler?

## Early return without error
Normal as a guard clause — "nothing to do" is a valid outcome (e.g. "if already processed, return").
Bug when the early return skips required work the caller depends on.
→ Check: do sibling implementations follow the same early-return pattern?
