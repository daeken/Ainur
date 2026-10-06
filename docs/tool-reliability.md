# Object and finder reliability

## Live PowerShell values

`Save-AinurObject -Value $value` and `Get-AinurObject -Handle $handle` preserve a
live session-owned object, not a JSON copy. Custom PowerShell objects keep their
`PSObject` wrapper: note properties, nested values, custom type names, and mutation
identity live there rather than on the underlying `PSCustomObject` marker.
Ordinary CLR objects and collections still use their underlying CLR identity.
The tool-function and Get wrappers use unary-comma output to avoid enumeration
without the scalar wrapping behavior of `Write-Output -NoEnumerate`.

Example:

```powershell
$obj = [pscustomobject]@{ Name = 'sample'; Nested = [pscustomobject]@{ Count = 7 } }
$handle = Save-AinurObject -Value $obj -Summary 'sample'
$copy = Get-AinurObject -Handle $handle
$copy.Nested.Count # 7
$copy.Name = 'changed'
$obj.Name # changed
```

At the tool **argument** boundary, custom objects recursively project their
properties into JSON objects, including custom values nested inside arrays or
hashtables. This does not change the registry's live-object storage or turn
arbitrary CLR values into custom-object property bags. Last-output registration
and tools that return live custom objects use the same wrapper preservation.
Session generation checks still reject handles after a session/runtime restart;
handles remain session-owned and ephemeral. No persistence or security checks
are relaxed by this fix.

## Finder answers and fallback

`find_tools` accepts a model selection only when all of the following hold:

- The provider reports `FinishReason == "stop"` and no tool calls.
- The entire response is one JSON object (no prose envelope, fenced JSON,
  truncated suffix, or first/last-brace extraction).
- `tools` is an array of at most four distinct string names from the offered
  candidates; `why` is a nonempty string.

An empty valid selection is allowed and is not replaced by keyword results.
Validation is atomic: a partly valid response cannot overwrite the initial local
ranking. A provider failure or invalid response returns the deterministic local
ranking with an explicit **keyword fallback** rationale. Selected fallback tools
are loaded only according to the caller's `load` argument. Cancellation continues
to propagate instead of being presented as a successful fallback.

Fallback is local tool discovery, **not another model dispatch** and not execution
of any suggested tool. The model gateway retains its existing billing-ambiguity
rules: an incomplete/ambiguous provider stream remains `unknown` and is not
silently retried. The real SSE finder tests cover complete-looking JSON at EOF,
`[DONE]` without authoritative completion, and valid `response.completed`.

The coordinated provider contract treats `response.completed` as authoritative
and returns immediately, without requiring EOF; `[DONE]` alone is not completion.
Provider terminal-open-tail/read-deadline behavior is owned by the bounded-runtime
change, not by this tool patch. Its independent deterministic test is
`BoundedModelTests.TerminalOpenTailDoesNotWaitForEof` (completed and DONE cases).
Validate both candidates together before activation; this patch does not mutate
provider code or introduce a second parser/retry layer.

## Offline regression checks

Run the focused suite:

`dotnet test tests/Ainur.Tests/Ainur.Tests.csproj --filter 'FullyQualifiedName~FinderReliabilityTests|FullyQualifiedName~PowerShellTests'`.

These regressions cover live custom-object property/type/identity preservation, nested custom arguments, malformed/incomplete finder rejection, explicit fallback labels, stale handles and composed scripts. They do not call live providers. Browser-dependent tests elsewhere require Chrome/Chromium; a focused pass is not a full-suite green claim. Test evidence must be attached to the exact candidate under review, not inferred from historical logs.
