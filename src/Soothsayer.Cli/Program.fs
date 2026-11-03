module Soothsayer.Cli.Program

open System
open System.IO
open Soothsayer.Core

let usage =
    """soothsayer: .NET API ground truth as seen from F#

Usage:
  soothsayer refs    <target>
  soothsayer search  <target> <query> [--kind k] [--limit n] [--scope primary|deps|all]
  soothsayer sig     <target> <FullName> [--no-docs]
  soothsayer outline <target> [--assembly name] [--scope primary|deps|all]
  soothsayer check   <target> <file.fsx|->
  soothsayer score   <target> <in.jsonl|-> [--workers n]   batch check: {"id","code"} per line in, JSON per line out
  soothsayer mcp                       run the MCP server on stdio

<target> is one of
  path/App.fsproj | path/App.csproj   restored references of the project; build it first so its own types show up.
                                      Multi-target projects: add #tfm, e.g. path/App.fsproj#net8.0
                                      (default: the highest netX.0).
  path/Lib.dll                        the dll plus sibling dlls and the framework reference pack
  nuget:Package.Id@version            restored into a cache (version optional = latest stable)"""

/// Splits argv into positional arguments and --flag [value] options.
let parse (argv: string list) =
    let rec go pos opts rest =
        match rest with
        | [] -> List.rev pos, opts
        | ("--no-docs" as f) :: tl -> go pos (Map.add f "" opts) tl
        | f :: v :: tl when f.StartsWith "--" -> go pos (Map.add f v opts) tl
        | f :: [] when f.StartsWith "--" -> failwithf "Option %s needs a value" f
        | x :: tl -> go (x :: pos) opts tl

    go [] Map.empty argv

[<EntryPoint>]
let main argv =
    try
        match List.ofArray argv with
        | []
        | ("-h" | "--help" | "help") :: _ ->
            printfn "%s" usage
            0
        | "mcp" :: _ -> McpHost.run ()
        | cmd :: rest ->
            let pos, opts = parse rest
            let engine = Engine()

            let notes (target: string) =
                for n in engine.Notes target do
                    eprintfn "note: %s" n

            let scope =
                opts |> Map.tryFind "--scope" |> Option.defaultValue "" |> Format.parseScope

            match cmd, pos with
            | "refs", [ target ] ->
                printfn "%s" (engine.Refs target |> Format.refs)
                0
            | "search", [ target; query ] ->
                let limit =
                    opts |> Map.tryFind "--limit" |> Option.map int |> Option.defaultValue 20

                let kind = opts |> Map.tryFind "--kind"
                printfn "%s" (engine.Search(target, query, kind, limit, scope) |> Format.hits)
                notes target
                0
            | "sig", [ target; name ] ->
                match engine.Signature(target, name, not (opts.ContainsKey "--no-docs")) with
                | Some s ->
                    printfn "%s" s
                    notes target
                    0
                | None ->
                    eprintfn "Nothing named '%s' found. Try: soothsayer search %s %s" name target name
                    1
            | "outline", [ target ] ->
                printfn "%s" (engine.Outline(target, Map.tryFind "--assembly" opts, scope))
                notes target
                0
            | "score", [ target; file ] ->
                let workers =
                    opts |> Map.tryFind "--workers" |> Option.map int |> Option.defaultValue 1

                Score.run target file workers
            | "check", [ target; file ] ->
                let code =
                    if file = "-" then
                        Console.In.ReadToEnd()
                    else
                        File.ReadAllText file

                let ds = engine.Check(target, code)
                printfn "%s" (Format.diagnostics ds)
                notes target

                if ds |> List.exists (fun d -> d.Severity = "error") then
                    1
                else
                    0
            | _ ->
                eprintfn "%s" usage
                2
    with ex ->
        eprintfn "error: %s" ex.Message
        1
