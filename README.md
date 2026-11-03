# Soothsayer

Ground truth about .NET APIs, as seen from F#, for people and coding agents.

Soothsayer loads the real reference set of a project, a dll or a NuGet package into the F# compiler (FSharp.Compiler.Service) and answers from it: what assemblies are in play, what a type or module looks like in `.fsi` syntax with its XML doc summaries, and whether a snippet of F# typechecks against those references. C# libraries are rendered in F# syntax, so `TorchSharp.torch.matmul` comes back as `static member matmul: input: Tensor * target: Tensor -> Tensor`.

The point is to stop agents (and people) writing calls from memory. Look the signature up, write the call, check it compiles, then present it.

## Targets

Every command takes a target:

- a `.fsproj` or `.csproj` path: its restored `ReferencePath` items, via `dotnet msbuild -getItem`. Multi-targeted projects use the highest `netX.Y` unless you pick one with `Proj.fsproj#net8.0`. The project's own types are visible only once it has been built; Soothsayer notes when the output dll is missing or older than the sources.
- a `.dll` path: the dll, its sibling dlls and the .NET reference pack
- `nuget:Package.Id@1.2.3` (version optional, latest stable otherwise): restored through a generated project under `~/.cache/soothsayer`, so transitive dependencies resolve

## CLI

```
soothsayer refs    <target>
soothsayer search  <target> <query> [--kind k] [--limit n] [--scope primary|deps|all]
soothsayer sig     <target> <FullName> [--no-docs]
soothsayer outline <target> [--assembly name] [--scope primary|deps|all]
soothsayer check   <target> <file.fsx|->
soothsayer mcp
```

`search` and `outline` look only at the target's own assemblies unless `--scope` widens them; `sig` searches everything. `check` exits 1 when there are errors.

```
$ soothsayer search nuget:TorchSharp squeeze --limit 2
[method] TorchSharp.torch.squeeze  (TorchSharp)
    static member squeeze: input: Tensor * ?dim: Nullable<int64> -> Tensor
[method] TorchSharp.torch.Tensor.squeeze  (TorchSharp)
    member squeeze: ?dim: Nullable<int64> -> Tensor
```

## MCP server

`soothsayer mcp` runs an MCP server on stdio with the tools `list_references`, `search_symbols`, `get_signature`, `assembly_outline` and `check_snippet`. The server keeps loaded reference sets in memory, so only the first call per target pays the load cost (5 to 9 seconds, plus a restore for a new NuGet target).

Register it with Claude Code:

```bash
claude mcp add soothsayer -- soothsayer mcp
```

## Build and install

Requires the .NET 10 SDK.

```bash
dotnet build Soothsayer.slnx -c Release
dotnet test Soothsayer.slnx -c Release
dotnet pack src/Soothsayer.Cli -c Release -o artifacts
dotnet tool install --global --add-source artifacts Soothsayer
```

The tests restore FSharp.Core and Newtonsoft.Json, so they need network access or a warm NuGet cache.

## Limitations

- Snippets are checked as scripts. `#r`, `#load` and `#I` are not resolved and produce warning SOOTH002, so put dependencies in the target.
- Only public API is shown. Protected members, overrides and explicit interface implementations are skipped, inherited members appear only as an `inherit` line, and C# extension methods appear as static members of their class.
- NuGet targets restore for `net10.0`; packages with no compatible asset fail with the restore error.
- A clean `check` means the code typechecks, not that it behaves correctly.

## History

The 2016 version was a VS Code extension written with Fable 0.7 on .NET Framework and mono. None of that stack survives, so this is a rewrite of the idea rather than an upgrade of the code.
