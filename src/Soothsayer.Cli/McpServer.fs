namespace Soothsayer.Cli

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Text
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol
open ModelContextProtocol.Server
open Soothsayer.Core

/// Text formatting shared by the CLI and the MCP tools, so both show the same thing.
module Format =

    let parseScope (s: string) =
        match (if isNull s then "" else s.Trim().ToLowerInvariant()) with
        | ""
        | "primary" -> PrimaryOnly
        | "deps"
        | "dependencies" -> WithDependencies
        | "all" -> AllReferences
        | other -> failwithf "Unknown scope '%s'; use primary, deps or all." other

    let refs (c: Catalog) =
        let sb = StringBuilder()
        sb.AppendLine(sprintf "Target: %s" c.Resolved.Description) |> ignore

        for w in c.Resolved.Warnings do
            sb.AppendLine("note: " + w) |> ignore

        sb.AppendLine(
            sprintf "%d referenced assemblies " c.Resolved.References.Length
            + "(role: primary = the target itself, dep = other packages/dlls, framework = reference pack)"
        )
        |> ignore

        for r in
            c.Resolved.References
            |> List.sortBy (fun r ->
                (match r.Role with
                 | Primary -> 0
                 | Dependency -> 1
                 | Framework -> 2),
                r.Name) do
            let role =
                match r.Role with
                | Primary -> "primary"
                | Dependency -> "dep"
                | Framework -> "framework"

            let pkg =
                match r.PackageId, r.PackageVersion with
                | Some p, Some v -> sprintf "  %s %s" p v
                | Some p, None -> "  " + p
                | _ -> ""

            sb.AppendLine(sprintf "%-9s %-45s%s" role r.Name pkg) |> ignore

        sb.ToString().TrimEnd()

    let hits (hs: SearchHit list) =
        if hs.IsEmpty then
            "No matches."
        else
            let sb = StringBuilder()

            for h in hs do
                sb.AppendLine(sprintf "[%s] %s  (%s)" h.Kind h.FullName h.Assembly) |> ignore
                sb.AppendLine("    " + h.Signature) |> ignore

                match h.Doc with
                | Some d -> sb.AppendLine("    /// " + d) |> ignore
                | None -> ()

            sb.ToString().TrimEnd()

    let diagnostics (ds: Diagnostic list) = Snippet.summarize ds

    /// Warnings about the target (project not built, output stale) go in front of any result.
    let withNotes (notes: string list) (text: string) =
        if notes.IsEmpty then
            text
        else
            String.Join("\n", notes |> List.map (fun n -> "note: " + n)) + "\n" + text

    /// Cuts at a line boundary and says how much was dropped, so a huge assembly cannot flood the context.
    let truncate (maxChars: int) (hint: string) (s: string) =
        if maxChars <= 0 || s.Length <= maxChars then
            s
        else
            let cut =
                (match s.LastIndexOf('\n', maxChars - 1) with
                 | i when i > maxChars / 2 -> i
                 | _ -> maxChars)

            sprintf "%s\n[truncated: showing %d of %d characters. %s]" (s.Substring(0, cut)) cut s.Length hint

/// Tool and parameter descriptions, written for the model that reads the tool list.
module private Descriptions =

    [<Literal>]
    let ListReferences =
        "List the assemblies that make up a target, so you know what API surface is available. A "
        + "target is one of: a path to a .fsproj/.csproj (uses its restored references, so run it "
        + "for the project you are editing), a path to a .dll, or 'nuget:Package.Id@1.2.3' (version "
        + "optional, defaults to the latest stable; the package is restored into a cache, which can "
        + "take several seconds the first time). For a multi-target project add #tfm after the path, "
        + "e.g. 'src/App/App.fsproj#net8.0' (default: the highest netX.0). Build the project first "
        + "so its own types are visible."

    [<Literal>]
    let SearchSymbols =
        "Find types, modules, functions, properties and methods by name, as F# sees them, instead "
        + "of guessing an API from memory. Case-insensitive; matches full names and member names, "
        + "e.g. 'List.map', 'Split', 'JsonConvert.Serialize'. Returns the F# signature and XML doc "
        + "summary of each hit, best matches first. Searches only the target's own assemblies by "
        + "default (the package, the dll, or a project's package references); set scope to 'deps' or "
        + "'all' to include dependencies or the .NET base class library."

    [<Literal>]
    let GetSignature =
        "Show the exact F# signature of a type, module, or a single member, in .fsi style with /// "
        + "doc comments. Pass a full or unique-suffix name such as 'System.String', "
        + "'Microsoft.FSharp.Collections.List' (shows both the List module and the List<'T> type), "
        + "or 'System.String.Split' / 'List.map' for one member and all its overloads. C# libraries "
        + "are rendered in F# syntax (member Foo: a: int * b: string -> bool). Use this before "
        + "writing a call, then verify the call with check_snippet."

    [<Literal>]
    let AssemblyOutline =
        "List the namespaces and the public types and modules of a target's assemblies (names and "
        + "kinds only, no members). Use it to discover what a package contains, then get_signature "
        + "for details. Large assemblies are truncated; pass an assembly name to narrow it."

    [<Literal>]
    let CheckSnippet =
        "Typecheck F# source code against the real references of a target and return compiler "
        + "diagnostics (line, column, severity, FS error code, message). Use it to verify generated "
        + "code compiles against the actual API before presenting it. The code is checked as a "
        + "script file (top-level expressions and 'open' are fine); '#r' and '#load' directives are "
        + "not resolved (a warning is returned); put dependencies in the target instead. Nothing is "
        + "executed. Zero errors means it typechecks, not that it behaves correctly."

    [<Literal>]
    let Target =
        "Target: .fsproj/.csproj path (optionally #tfm, e.g. App.fsproj#net8.0), .dll path, or "
        + "nuget:Package.Id@version."

    [<Literal>]
    let CheckTarget =
        "Target whose references the code is checked against: .fsproj/.csproj path (optionally "
        + "#tfm), .dll path, or nuget:Package.Id@version."

    [<Literal>]
    let Query =
        "Text to look for, e.g. 'List.map' or 'matmul'. Generic arity like List`1 or <'T> is "
        + "ignored."

    [<Literal>]
    let Kind =
        "Optional filter: class, record, union, struct, enum, interface, delegate, abbreviation, "
        + "exception, measure, module, or member kinds ctor, property, method, function, value, "
        + "case, field, event."

    [<Literal>]
    let Scope =
        "'primary' (default), 'deps' (adds package dependencies) or 'all' (adds the framework "
        + "reference assemblies)."

    [<Literal>]
    let Assembly =
        "Optional assembly simple name from list_references; also reaches framework assemblies."

    [<Literal>]
    let Name = "Full name of a type/module, or Type.Member. Generic arity is ignored."

    [<Literal>]
    let IncludeDocs = "Include /// doc summaries (default true)."

    [<Literal>]
    let ScopeShort = "'primary' (default), 'deps' or 'all'."

[<McpServerToolType>]
type SoothsayerTools(engine: Engine) =

    let fail (ex: exn) : 'a =
        let rec root (e: exn) =
            if isNull e.InnerException then e else root e.InnerException

        raise (McpException(sprintf "soothsayer: %s" (root ex).Message))

    let guard (f: unit -> string) =
        try
            f ()
        with
        | :? McpException -> reraise ()
        | ex -> fail ex

    [<McpServerTool(Name = "list_references", ReadOnly = true, Idempotent = true);
      Description(Descriptions.ListReferences)>]
    member _.ListReferences([<Description(Descriptions.Target)>] target: string) : string =
        guard (fun () -> engine.Refs target |> Format.refs)

    [<McpServerTool(Name = "search_symbols", ReadOnly = true, Idempotent = true);
      Description(Descriptions.SearchSymbols)>]
    member _.SearchSymbols
        (
            [<Description(Descriptions.Target)>] target: string,
            [<Description(Descriptions.Query)>] query: string,
            [<Optional; DefaultParameterValue(null: string); Description(Descriptions.Kind)>] kind: string,
            [<Optional; DefaultParameterValue(20); Description("Maximum number of results (default 20).")>] limit: int,
            [<Optional; DefaultParameterValue(null: string); Description(Descriptions.Scope)>] scope: string
        ) : string =
        guard (fun () ->
            let k = if String.IsNullOrWhiteSpace kind then None else Some kind

            engine.Search(target, query, k, (if limit <= 0 then 20 else min limit 100), Format.parseScope scope)
            |> Format.hits
            |> Format.truncate 12000 "Lower limit or make the query more specific."
            |> Format.withNotes (engine.Notes target))

    [<McpServerTool(Name = "get_signature", ReadOnly = true, Idempotent = true)>]
    [<Description(Descriptions.GetSignature)>]
    member _.GetSignature
        (
            [<Description(Descriptions.Target)>] target: string,
            [<Description(Descriptions.Name)>] name: string,
            [<Optional; DefaultParameterValue(true); Description(Descriptions.IncludeDocs)>] includeDocs: bool
        ) : string =
        guard (fun () ->
            match engine.Signature(target, name, includeDocs) with
            | Some s ->
                Format.truncate 16000 "Ask for a specific member, e.g. Type.Member, or call with includeDocs=false." s
                |> Format.withNotes (engine.Notes target)
            | None -> sprintf "Nothing named '%s' was found. Use search_symbols to find the right name." name)

    [<McpServerTool(Name = "assembly_outline", ReadOnly = true, Idempotent = true);
      Description(Descriptions.AssemblyOutline)>]
    member _.AssemblyOutline
        (
            [<Description(Descriptions.Target)>] target: string,
            [<Optional; DefaultParameterValue(null: string); Description(Descriptions.Assembly)>] assembly: string,
            [<Optional; DefaultParameterValue(null: string); Description(Descriptions.ScopeShort)>] scope: string
        ) : string =
        guard (fun () ->
            let a =
                if String.IsNullOrWhiteSpace assembly then
                    None
                else
                    Some assembly

            engine.Outline(target, a, Format.parseScope scope)
            |> Format.truncate 12000 "Pass an assembly name to see one assembly, or use search_symbols."
            |> Format.withNotes (engine.Notes target))

    [<McpServerTool(Name = "check_snippet", ReadOnly = true, Idempotent = true)>]
    [<Description(Descriptions.CheckSnippet)>]
    member _.CheckSnippet
        ([<Description(Descriptions.CheckTarget)>] target: string, [<Description("F# source text.")>] code: string)
        : string =
        guard (fun () ->
            engine.Check(target, code)
            |> Format.diagnostics
            |> Format.truncate 8000 "Fix the first errors and re-run."
            |> Format.withNotes (engine.Notes target))

module McpHost =
    let run () : int =
        let builder = Host.CreateApplicationBuilder([||])
        // stdout carries the protocol, so every log line must go to stderr.
        builder.Logging.ClearProviders() |> ignore

        builder.Logging.AddConsole(fun o -> o.LogToStandardErrorThreshold <- LogLevel.Trace)
        |> ignore

        builder.Logging.SetMinimumLevel LogLevel.Warning |> ignore
        builder.Services.AddSingleton<Engine>() |> ignore

        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<SoothsayerTools>()
        |> ignore

        builder.Build().Run()
        0
