module Soothsayer.Tests.Tests

open System
open Xunit
open Soothsayer.Core

// One engine for the whole run: FCS startup and package restore dominate the cost.
let engine = Engine()

let fsharpCore = "nuget:FSharp.Core@10.1.403"
let newtonsoft = "nuget:Newtonsoft.Json@13.0.3"

let contains (needle: string) (haystack: string) = haystack.Contains(needle, StringComparison.Ordinal)

[<Fact>]
let ``List.map has the expected F# signature`` () =
    let hits = engine.Search(fsharpCore, "List.map", Some "function", 5, PrimaryOnly)
    let top = hits.Head
    Assert.Equal("Microsoft.FSharp.Collections.List.map", top.FullName)
    // parameter names are not part of the contract; strip "name: " before comparing
    let afterName = top.Signature.Substring("val map: ".Length)
    let stripped = System.Text.RegularExpressions.Regex.Replace(afterName, @"\b\w+: ", "")
    Assert.StartsWith("val map: ", top.Signature)
    Assert.Equal("('T -> 'U) -> 'T list -> 'U list", stripped)
    Assert.True(top.Doc.IsSome, "expected the XML doc summary from FSharp.Core.xml")

[<Fact>]
let ``Option module exists and renders as a module`` () =
    let text = engine.Signature(fsharpCore, "Microsoft.FSharp.Core.Option", true).Value
    Assert.Contains("module Option =", text)
    Assert.Contains("val inline map:", text)
    // the Option<'T> type with its cases is found under the same name
    Assert.Contains("| Some of Value: 'T", text)

[<Fact>]
let ``Union cases and doc comments are rendered`` () =
    let text = engine.Signature(fsharpCore, "Microsoft.FSharp.Core.Result", true).Value
    Assert.Contains("| Ok of ResultValue: 'T", text)
    Assert.Contains("/// ", text)

[<Fact>]
let ``System.String from the BCL renders in F# syntax with Split`` () =
    let text = engine.Signature(fsharpCore, "System.String", false).Value
    Assert.Contains("type String =", text)
    Assert.Contains("member Split: separator: char * ?options: StringSplitOptions -> string array", text)
    Assert.Contains("static member Join:", text)

[<Fact>]
let ``Single member lookup shows all overloads`` () =
    let text = engine.Signature(fsharpCore, "System.String.Split", true).Value
    let n = text.Split('\n') |> Array.filter (fun l -> l.TrimStart().StartsWith "member Split:") |> Array.length
    Assert.True(n >= 5, sprintf "expected several Split overloads, got %d" n)

[<Fact>]
let ``C# library is rendered with F# syntax`` () =
    let text = engine.Signature(newtonsoft, "Newtonsoft.Json.JsonConvert", true).Value
    Assert.Contains("type JsonConvert =", text)
    Assert.Contains("static member SerializeObject: value: obj -> string", text)
    // F# keyword used as a parameter name must be escaped
    Assert.Contains("``type``: Type", text)
    Assert.Contains("[<ParamArray>] converters: JsonConverter array", text)

[<Fact>]
let ``Search finds a C# method and kind filter works`` () =
    let hits = engine.Search(newtonsoft, "DeserializeObject", Some "method", 3, PrimaryOnly)
    Assert.NotEmpty hits
    Assert.All(hits, fun h -> Assert.Equal("method", h.Kind))
    Assert.All(hits, fun h -> Assert.Contains("DeserializeObject", h.FullName))

[<Fact>]
let ``Search of a nonsense name returns nothing`` () =
    Assert.Empty(engine.Search(fsharpCore, "zzzqqqxxx", None, 5, PrimaryOnly))

[<Fact>]
let ``dll target resolves siblings and the framework`` () =
    let dll = typeof<Microsoft.FSharp.Core.Unit>.Assembly.Location
    let hits = engine.Search(dll, "List.map", Some "function", 1, PrimaryOnly)
    Assert.Equal("Microsoft.FSharp.Collections.List.map", hits.Head.FullName)
    let ds = engine.Check(dll, "let s = System.String.Join(\",\", [\"a\"; \"b\"])")
    Assert.Empty(ds |> List.filter (fun d -> d.Severity = "error"))
    let broken = engine.Check(dll, "let s = System.String.Joinn(\",\", [\"a\"])")
    Assert.NotEmpty(broken |> List.filter (fun d -> d.Severity = "error"))

[<Fact>]
let ``valid snippet has zero errors`` () =
    let ds = engine.Check(fsharpCore, "let xs = [1; 2; 3]\nlet ys = List.map (fun x -> x * 2) xs\nprintfn \"%A\" ys\n")
    Assert.Empty(ds |> List.filter (fun d -> d.Severity = "error"))
    let broken = engine.Check(fsharpCore, "let xs = [1; 2; 3]\nlet ys = List.map (fun x -> x * 2) \"nope\"\n")
    Assert.NotEmpty(broken |> List.filter (fun d -> d.Severity = "error"))

[<Fact>]
let ``List.mapp is an error at the right position`` () =
    let ds = engine.Check(fsharpCore, "let ys = List.mapp (fun x -> x) [1]\n")
    let errors = ds |> List.filter (fun d -> d.Severity = "error")
    Assert.NotEmpty errors
    let e = errors.Head
    Assert.Equal("FS0039", e.Code)
    Assert.Equal(1, e.Line)
    Assert.Equal(15, e.Column)
    Assert.Contains("mapp", e.Message)

[<Fact>]
let ``type error against a C# API is reported`` () =
    let ds = engine.Check(newtonsoft, "let s : int = Newtonsoft.Json.JsonConvert.SerializeObject 1\n")
    Assert.Contains(ds, fun d -> d.Severity = "error" && (d.Code = "FS0001" || d.Code = "FS0193") && d.Message.Contains "int")

[<Fact>]
let ``snippet checking is repeatable on one engine`` () =
    // the version counter must make FCS re-check changed text for the same file name
    let bad = engine.Check(fsharpCore, "let x : string = 1\n")
    let good = engine.Check(fsharpCore, "let x : string = \"1\"\n")
    Assert.NotEmpty(bad |> List.filter (fun d -> d.Severity = "error"))
    Assert.Empty(good |> List.filter (fun d -> d.Severity = "error"))

[<Fact>]
let ``project target resolves references of the Core project`` () =
    let rec findRoot (d: IO.DirectoryInfo) =
        if IO.File.Exists(IO.Path.Combine(d.FullName, "Soothsayer.slnx")) then d.FullName else findRoot d.Parent
    let root = findRoot (IO.DirectoryInfo AppContext.BaseDirectory)
    let proj = IO.Path.Combine(root, "src", "Soothsayer.Core", "Soothsayer.Core.fsproj")
    let text = engine.Signature(proj, "FSharp.Compiler.CodeAnalysis.FSharpChecker", false).Value
    Assert.Contains("type FSharpChecker =", text)
    Assert.Contains("member ParseAndCheckProject:", text)

[<Fact>]
let ``unknown target form is rejected with a helpful message`` () =
    let ex = Assert.ThrowsAny<exn>(fun () -> engine.Refs "nonsense" |> ignore)
    Assert.Contains("nuget:", ex.Message)

[<Fact>]
let ``search ranks exact suffix match first`` () =
    let s1 = Search.score "List.map" "Microsoft.FSharp.Collections.List.map"
    let s2 = Search.score "List.map" "Microsoft.FSharp.Collections.List.mapi"
    Assert.True(s1 > s2 && s2 > 0)

// ---------------------------------------------------------------------------------------------
// Review fixes. Fixtures that need a compiler or msbuild are generated into temp directories.
// ---------------------------------------------------------------------------------------------

let private dotnet (workDir: string) (args: string list) =
    let code, out, err = Targets.runProcess "dotnet" args workDir (TimeSpan.FromMinutes 5.0)
    if code <> 0 then failwithf "dotnet %s failed:\n%s\n%s" (String.Join(" ", args)) out err
    out

let private tempDir (name: string) =
    let d = IO.Path.Combine(IO.Path.GetTempPath(), "sooth-tests", Guid.NewGuid().ToString("N").Substring(0, 8), name)
    IO.Directory.CreateDirectory d |> ignore
    d

let csSource =
    """namespace Fx;
public class Holder<T> where T : struct
{
    public int P { get; internal set; }
    public int Q { get; set; }
    public int R { get; protected set; }
    [System.Obsolete("use New")] public void Old() { }
    [System.Obsolete("old prop")] public int OldProp { get; set; }
    public Holder(T seed) { }
}
"""

/// A small C# library with the shapes the review cared about.
let csLib =
    lazy
        (let dir = tempDir "cslib"
         IO.File.WriteAllText(
             IO.Path.Combine(dir, "Fx.csproj"),
             "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")
         IO.File.WriteAllText(IO.Path.Combine(dir, "Fx.cs"), csSource)
         let outDir = IO.Path.Combine(dir, "out")
         dotnet dir [ "build"; "-c"; "Release"; "-o"; outDir; "-nologo"; "-v:q" ] |> ignore
         IO.Path.Combine(dir, "out", "Fx.dll"))

[<Fact>]
let ``constructors render the declaring type, not Unit`` () =
    let uri = engine.Signature(fsharpCore, "System.Uri", false).Value
    Assert.Contains("new: uriString: string -> Uri", uri)
    Assert.DoesNotContain("-> Unit", uri)
    let dict = engine.Signature(fsharpCore, "System.Collections.Generic.Dictionary", false).Value
    Assert.Contains("new: capacity: int -> Dictionary<'TKey,'TValue>", dict)
    Assert.DoesNotContain("-> Unit", dict)

[<Fact>]
let ``type-level constraints are shown`` () =
    let set = engine.Signature(fsharpCore, "Microsoft.FSharp.Collections.Set", false).Value
    Assert.Contains("type Set<'T when 'T : comparison> =", set)
    let map = engine.Signature(fsharpCore, "Microsoft.FSharp.Collections.Map", false).Value
    Assert.Contains("type Map<'Key,'Value when 'Key : comparison> =", map)
    // C# `where T : struct`
    let holder = engine.Signature(csLib.Value, "Fx.Holder", false).Value
    Assert.Contains("'T : struct", holder)
    Assert.Contains("type Holder<'T when 'T", holder)

[<Fact>]
let ``delegates render their signature`` () =
    let func = engine.Signature(fsharpCore, "System.Func", false).Value
    Assert.Contains("type Func<'T,'TResult> = delegate of arg: 'T -> 'TResult", func)
    let action = engine.Signature(fsharpCore, "System.Action", false).Value
    Assert.Contains("type Action<'T> = delegate of obj: 'T -> unit", action)
    Assert.DoesNotContain("delegate of ?", func)
    Assert.DoesNotContain("unavailable", func)

[<Fact>]
let ``multi-target project resolves, honouring a tfm suffix`` () =
    let dir = tempDir "multi tfm"
    let proj = IO.Path.Combine(dir, "Multi.csproj")
    IO.File.WriteAllText(proj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>")
    IO.File.WriteAllText(IO.Path.Combine(dir, "C.cs"), "namespace M; public class C { public int X => 1; }")
    let refsOf (t: string) = (engine.Catalog t).Resolved.References |> List.map (fun r -> r.Path)
    let def = refsOf proj
    Assert.Contains(def, fun p -> p.Contains "ref/net10.0")
    let n8 = refsOf (proj + "#net8.0")
    Assert.Contains(n8, fun p -> p.Contains "ref/net8.0")
    Assert.DoesNotContain(n8, fun p -> p.Contains "ref/net10.0")
    let n8b = refsOf (proj + "@net8.0")
    Assert.Contains(n8b, fun p -> p.Contains "ref/net8.0")
    // unbuilt project: the user must be told to build
    let warnings = (engine.Catalog proj).Resolved.Warnings
    Assert.Contains(warnings, fun w -> w.Contains "build")

[<Theory>]
[<InlineData("nuget:X\"/><Target Name=\"x\"")>]
[<InlineData("nuget:Newtonsoft.Json@1.0\"><Exec Command=\"touch /tmp/pwn\" />")>]
[<InlineData("nuget:Id With Space")>]
[<InlineData("nuget:")>]
let ``hostile nuget ids and versions are rejected before anything is written`` (target: string) =
    let ex = Assert.ThrowsAny<exn>(fun () -> engine.Refs target |> ignore)
    Assert.Contains("Invalid NuGet", ex.Message)

[<Fact>]
let ``catalog notices a changed dll`` () =
    let dir = tempDir "stale"
    let target = IO.Path.Combine(dir, "lib.dll")
    let nj = engine.Catalog(newtonsoft).Resolved.References |> List.find (fun r -> r.Name = "Newtonsoft.Json")
    IO.File.Copy(nj.Path, target)
    Assert.NotEmpty(engine.Search(target, "JsonConvert", Some "class", 3, PrimaryOnly))
    // replace with a different assembly under the same path and a later mtime
    IO.File.Copy(typeof<Engine>.Assembly.Location, target, true)
    IO.File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes 5.0)
    Assert.Empty(engine.Search(target, "JsonConvert", Some "class", 3, PrimaryOnly))
    Assert.NotEmpty(engine.Search(target, "Soothsayer.Core.Engine", Some "class", 3, PrimaryOnly))

[<Fact>]
let ``process runner kills a hung child at the timeout`` () =
    let sw = Diagnostics.Stopwatch.StartNew()
    Assert.ThrowsAny<exn>(fun () -> Targets.runProcess "sleep" [ "20" ] "." (TimeSpan.FromSeconds 1.0) |> ignore) |> ignore
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds 10.0, sprintf "took %A" sw.Elapsed)

[<Fact>]
let ``obsolete members, partial setters and struct unions render faithfully`` () =
    let text = engine.Signature(csLib.Value, "Fx.Holder", false).Value
    Assert.Contains("[<Obsolete>] member Old: unit -> unit", text)
    Assert.Contains("[<Obsolete>] member OldProp: int with get, set", text)
    Assert.Contains("member P: int with get\n", text + "\n")
    Assert.Contains("member Q: int with get, set", text)
    // protected setter is not public API
    Assert.Contains("member R: int with get\n", text + "\n")
    let vo = engine.Signature(fsharpCore, "Microsoft.FSharp.Core.ValueOption", false).Value
    Assert.Contains("[<Struct>]", vo)

[<Fact>]
let ``entity truncation is announced`` () =
    let text = engine.Signature(fsharpCore, "Microsoft.FSharp.Core.Option", false, 1).Value
    Assert.Contains("more matching type(s) not shown", text)

[<Fact>]
let ``directives produce a warning and the summary is unambiguous`` () =
    let ds = engine.Check(fsharpCore, "#r \"nuget: Newtonsoft.Json\"\nlet x = 1\n")
    let w = ds |> List.filter (fun d -> d.Severity = "warning" && d.Message.Contains "directives are not resolved")
    Assert.NotEmpty w
    let summary = Snippet.summarize ds
    Assert.Contains("No errors; ", summary)
    Assert.DoesNotContain("OK", summary)
    Assert.Equal("No errors; 0 warning(s).", Snippet.summarize [])
    Assert.StartsWith("1 error(s)", Snippet.summarize [ { Severity = "error"; Line = 1; Column = 1; EndLine = 1; EndColumn = 2; Code = "FS0001"; Message = "m" } ])
