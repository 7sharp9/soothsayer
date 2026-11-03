/// Turns a set of reference dlls into FCS project options and the FSharpAssemblySignature of each referenced assembly.
module Soothsayer.Core.Loader

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Collections.Generic
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

type LoadedAssembly =
    { Name: string
      Path: string option
      Signature: FSharpAssemblySignature
      Reference: Reference option
      Docs: XmlDocs.DocFile }

type LoadedSet =
    { Hash: string
      Options: FSharpProjectOptions
      /// The file that stands in for the snippet in ParseAndCheckFileInProject.
      SnippetFile: string
      Assemblies: LoadedAssembly list }

let referenceSetHash (refs: Reference list) =
    let sb = StringBuilder()
    for r in refs |> List.sortBy (fun r -> r.Path) do
        let info = FileInfo r.Path
        // length + mtime so a rebuilt dll under the same path gets a new key
        let stamp = if info.Exists then sprintf "%d:%d" info.Length info.LastWriteTimeUtc.Ticks else "missing"
        sb.Append(r.Path).Append('|').Append(stamp).Append('\n') |> ignore
    use sha = SHA256.Create()
    Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16).ToLowerInvariant()

type Loader(checker: FSharpChecker) =
    // per-hash lazies: loading one reference set must not block loading another
    let cache = System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<LoadedSet>>()
    let workRoot = Path.Combine(Path.GetTempPath(), "soothsayer")

    member _.Checker = checker

    member _.Load(refs: Reference list) : LoadedSet =
        let hash = referenceSetHash refs
        let create () =
            lazy
                (
                let dir = Path.Combine(workRoot, hash)
                Directory.CreateDirectory dir |> ignore
                // .fsx so that top-level snippets without a module declaration are accepted.
                let file = Path.Combine(dir, "snippet.fsx")
                File.WriteAllText(file, "module Snippet\n")
                let args =
                    [| yield "--noframework"
                       yield "--targetprofile:netcore"
                       yield "--simpleresolution"
                       yield "--nologo"
                       yield "--fullpaths"
                       yield "--flaterrors"
                       yield "--target:exe"
                       yield "--warn:3"
                       yield "--define:INTERACTIVE"
                       for r in refs -> "-r:" + r.Path
                       yield file |]
                let opts =
                    checker.GetProjectOptionsFromCommandLineArgs(Path.Combine(dir, "snippet.fsproj"), args)
                let results = checker.ParseAndCheckProject(opts) |> Async.RunSynchronously
                let byPath =
                    refs |> List.map (fun r -> Path.GetFullPath r.Path, r) |> dict
                let loaded =
                    results.ProjectContext.GetReferencedAssemblies()
                    |> List.map (fun a ->
                        let path = a.FileName |> Option.map Path.GetFullPath
                        let reference =
                            path |> Option.bind (fun p -> match byPath.TryGetValue p with | true, r -> Some r | _ -> None)
                        { Name = a.SimpleName
                          Path = path
                          Signature = a.Contents
                          Reference = reference
                          Docs =
                            match path with
                            | Some p -> XmlDocs.forDll p
                            | None -> XmlDocs.forDll "" })
                { Hash = hash; Options = opts; SnippetFile = file; Assemblies = loaded })
        let l = cache.GetOrAdd(hash, fun _ -> create ())
        try l.Value
        with _ ->
            // a failed load must not be cached forever
            cache.TryRemove hash |> ignore
            reraise ()
