/// Typechecks an F# source string against a loaded reference set.
module Soothsayer.Core.Snippet

open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open Soothsayer.Core.Loader

let private version = ref 0

let private directivePattern =
    System.Text.RegularExpressions.Regex(@"^\s*#(r|load|I)\b", System.Text.RegularExpressions.RegexOptions.Compiled)

let private severity (s: FSharpDiagnosticSeverity) =
    match s with
    | FSharpDiagnosticSeverity.Error -> "error"
    | FSharpDiagnosticSeverity.Warning -> "warning"
    | FSharpDiagnosticSeverity.Info -> "info"
    | FSharpDiagnosticSeverity.Hidden -> "hidden"

/// Lines are 1-based. Columns are reported 1-based (FCS uses 0-based).
let check (loader: Loader) (set: LoadedSet) (code: string) : Diagnostic list =
    let v = Interlocked.Increment version

    let parse, answer =
        loader.Checker.ParseAndCheckFileInProject(set.SnippetFile, v, SourceText.ofString code, set.Options)
        |> Async.RunSynchronously

    let checkDiags =
        match answer with
        | FSharpCheckFileAnswer.Succeeded r -> r.Diagnostics
        | FSharpCheckFileAnswer.Aborted -> [||]

    let aborted =
        (match answer with
         | FSharpCheckFileAnswer.Aborted -> true
         | _ -> false)

    let all = Array.append parse.Diagnostics checkDiags |> Array.distinct
    // #r/#load/#I are script-resolution features; this checker is a project, so nothing is resolved. Say so.
    let directives =
        code.Replace("\r\n", "\n").Split('\n')
        |> Array.mapi (fun i l -> i + 1, l)
        |> Array.filter (fun (_, l) -> directivePattern.IsMatch l)
        |> Array.map (fun (n, l) ->
            {
                Severity = "warning"
                Line = n
                Column = 1
                EndLine = n
                EndColumn = l.TrimEnd().Length + 1
                Code = "SOOTH002"
                Message =
                    "'"
                    + l.Trim()
                    + "': directives are not resolved; put the dependency in the target "
                    + "(use a nuget: or project target) and remove the directive."
            })
        |> Array.toList

    [
        for d in all do
            if d.Severity <> FSharpDiagnosticSeverity.Hidden then
                yield
                    {
                        Severity = severity d.Severity
                        Line = d.StartLine
                        Column = d.StartColumn + 1
                        EndLine = d.EndLine
                        EndColumn = d.EndColumn + 1
                        Code = d.ErrorNumberText
                        Message = d.Message
                    }
        if aborted then
            yield
                {
                    Severity = "error"
                    Line = 1
                    Column = 1
                    EndLine = 1
                    EndColumn = 1
                    Code = "SOOTH001"
                    Message = "Type checking was aborted (checker was busy or the project could not be built)."
                }
    ]
    |> fun fcs -> fcs @ directives
    |> List.sortBy (fun d -> d.Line, d.Column)


/// Like `check`, but stops after parsing when the parse has errors, as fsc does.
/// Typechecking a recovered tree adds errors that are artefacts of the syntax error,
/// which would distort error-class counts.
let checkParseFirst (loader: Loader) (set: LoadedSet) (code: string) : Diagnostic list =
    let parsingOptions, _ =
        loader.Checker.GetParsingOptionsFromProjectOptions set.Options

    let parse =
        loader.Checker.ParseFile(set.SnippetFile, SourceText.ofString code, parsingOptions, cache = false)
        |> Async.RunSynchronously

    if not parse.ParseHadErrors then
        check loader set code
    else
        [
            for d in parse.Diagnostics do
                if d.Severity <> FSharpDiagnosticSeverity.Hidden then
                    yield
                        {
                            Severity = severity d.Severity
                            Line = d.StartLine
                            Column = d.StartColumn + 1
                            EndLine = d.EndLine
                            EndColumn = d.EndColumn + 1
                            Code = d.ErrorNumberText
                            Message = d.Message
                        }
        ]
        |> List.sortBy (fun d -> d.Line, d.Column)

/// Text summary of a check: counts first so a warnings-only result cannot be mistaken for a clean one.
let summarize (ds: Diagnostic list) : string =
    let errors = ds |> List.filter (fun d -> d.Severity = "error") |> List.length
    let warnings = ds |> List.filter (fun d -> d.Severity = "warning") |> List.length

    let head =
        if errors = 0 then
            sprintf "No errors; %d warning(s)." warnings
        else
            sprintf "%d error(s), %d warning(s). Line and column are 1-based." errors warnings

    let lines =
        ds
        |> List.map (fun d ->
            sprintf "%d:%d-%d:%d %s %s: %s" d.Line d.Column d.EndLine d.EndColumn d.Severity d.Code d.Message)

    String.concat "\n" (head :: lines)
