/// Batch typechecking for evaluations: JSONL in ({"id": ..., "code": "..."} per line),
/// JSONL out, one result per snippet.
module Soothsayer.Cli.Score

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Soothsayer.Core

// Error classes, by FS code. Anything not listed is "other".
let private unknownName = set [ "FS0039" ]

let private apiMisuse =
    set
        [
            "FS0001"
            "FS0041"
            "FS0193"
            "FS0501"
            "FS0502"
            "FS0503"
            "FS0508"
            "FS0017"
            "FS0366"
            "FS0855"
        ]

let private syntax =
    set [ "FS0010"; "FS0583"; "FS0588"; "FS0003"; "FS0597"; "FS1241" ]

let private classify (code: string) =
    if unknownName.Contains code then "unknown_name"
    elif apiMisuse.Contains code then "api_misuse"
    elif syntax.Contains code then "syntax"
    else "other"

// FS0039 names the missing identifier last: "... member named 'x'" or "... type 'x' is not defined".
let private missingName =
    Regex(@"named '([^']+)'|'([^']+)' is not defined", RegexOptions.Compiled)

let private identifierOf (d: Diagnostic) =
    let m = missingName.Match d.Message

    if not m.Success then None
    elif m.Groups.[1].Success then Some m.Groups.[1].Value
    else Some m.Groups.[2].Value

let private resultJson (id: JsonElement) (ds: Diagnostic list) (ms: float) (cached: bool) =
    let errors = ds |> List.filter (fun d -> d.Severity = "error")
    let classes = errors |> List.countBy (fun d -> classify d.Code) |> dict

    let get k =
        match classes.TryGetValue k with
        | true, n -> n
        | _ -> 0

    let unknown =
        errors
        |> List.filter (fun d -> unknownName.Contains d.Code)
        |> List.choose identifierOf
        |> List.distinct

    let payload =
        {|
            id = id
            ok = errors.IsEmpty
            errors = errors.Length
            warnings = ds |> List.filter (fun d -> d.Severity = "warning") |> List.length
            unknown_name = get "unknown_name"
            api_misuse = get "api_misuse"
            syntax = get "syntax"
            other = get "other"
            unknown_identifiers = unknown
            diagnostics =
                ds
                |> List.map (fun d ->
                    {|
                        severity = d.Severity
                        code = d.Code
                        line = d.Line
                        column = d.Column
                        message = d.Message
                    |})
            ms = Math.Round(ms, 2)
            cached = cached
        |}

    JsonSerializer.Serialize payload

/// Checks every line of `input` in this process. Identical code is checked once.
let private runSingle (engine: Engine) (target: string) (input: TextReader) (output: TextWriter) =
    let c = engine.Catalog target
    let seen = Dictionary<string, Diagnostic list>(StringComparer.Ordinal)
    let mutable n = 0
    let mutable line = input.ReadLine()

    while not (isNull line) do
        if not (String.IsNullOrWhiteSpace line) then
            use doc = JsonDocument.Parse line
            let root = doc.RootElement
            let id = root.GetProperty("id").Clone()
            let code = root.GetProperty("code").GetString()
            let sw = Stopwatch.StartNew()

            let ds, cached =
                match seen.TryGetValue code with
                | true, ds -> ds, true
                | _ ->
                    let ds = Snippet.checkParseFirst engine.Loader c.Set code
                    seen.[code] <- ds
                    ds, false

            output.WriteLine(resultJson id ds sw.Elapsed.TotalMilliseconds cached)
            n <- n + 1

        line <- input.ReadLine()

    output.Flush()
    n

/// How to start another copy of this tool (as an apphost, or as `dotnet soothsayer.dll`).
let private selfCommand () =
    let exe = Environment.ProcessPath

    if Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) then
        exe, [ Reflection.Assembly.GetEntryAssembly().Location ]
    else
        exe, []

/// Splits the input across `workers` child processes, each holding its own warm checker.
/// One FSharpChecker checks one snippet file at a time, so parallelism is per process. Output order is not preserved.
let private runParallel (target: string) (workers: int) (input: TextReader) (output: TextWriter) =
    let exe, prefix = selfCommand ()

    let procs =
        [
            for _ in 1..workers ->
                let psi = ProcessStartInfo(exe)

                for a in prefix @ [ "score"; target; "-" ] do
                    psi.ArgumentList.Add a

                psi.RedirectStandardInput <- true
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- false
                psi.UseShellExecute <- false
                Process.Start psi
        ]

    let gate = obj ()
    let mutable n = 0

    let readers =
        procs
        |> List.map (fun p ->
            Threading.Tasks.Task.Run(fun () ->
                let mutable l = p.StandardOutput.ReadLine()

                while not (isNull l) do
                    lock gate (fun () ->
                        output.WriteLine l
                        n <- n + 1)

                    l <- p.StandardOutput.ReadLine()))

    let mutable i = 0
    let mutable line = input.ReadLine()

    while not (isNull line) do
        if not (String.IsNullOrWhiteSpace line) then
            procs.[i % workers].StandardInput.WriteLine line
            i <- i + 1

        line <- input.ReadLine()

    for p in procs do
        p.StandardInput.Close()

    Threading.Tasks.Task.WaitAll(readers |> List.toArray)

    for p in procs do
        p.WaitForExit()

    output.Flush()
    n

/// Entry point for `soothsayer score <target> <in.jsonl|-> [--workers n]`. Summary goes to stderr.
let run (target: string) (file: string) (workers: int) =
    let sw = Stopwatch.StartNew()

    use input =
        if file = "-" then
            Console.In
        else
            new StreamReader(file) :> TextReader

    let output = Console.Out

    let n =
        if workers <= 1 then
            runSingle (Engine()) target input output
        else
            runParallel target workers input output

    let secs = sw.Elapsed.TotalSeconds

    eprintfn
        "scored %d snippet(s) in %.2fs (%.1f/s, %d worker(s), including startup)"
        n
        secs
        (float n / secs)
        (max 1 workers)

    0
