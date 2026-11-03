/// High-level operations shared by the CLI and the MCP server.
namespace Soothsayer.Core

open System
open System.Collections.Generic
open FSharp.Compiler.CodeAnalysis
open Soothsayer.Core.Loader
open Soothsayer.Core

type Catalog =
    { Resolved: ResolvedTarget
      Set: LoadedSet
      /// Entities per assembly, keyed by assembly simple name.
      Entities: Lazy<Map<string, Entity list>>
      /// Stat fingerprint of Resolved.Watch taken when this catalog was built.
      Fingerprint: string
      Created: DateTime }

type Engine() =
    let checker = FSharpChecker.Create(projectCacheSize = 20, keepAssemblyContents = false, keepAllBackgroundResolutions = false)
    let loader = Loader(checker)
    let catalogs = System.Collections.Concurrent.ConcurrentDictionary<string, Catalog>(StringComparer.Ordinal)
    // one gate per target: a slow restore of one package must not block queries against another
    let gates = System.Collections.Concurrent.ConcurrentDictionary<string, obj>(StringComparer.Ordinal)

    /// Cache key: nuget specs are case-insensitive; file targets are absolute paths, keeping any #tfm suffix.
    let normalise (target: string) =
        let t = target.Trim()
        if t.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) then t.ToLowerInvariant()
        else
            match Targets.splitProjectTarget t with
            | Some(p, tfm) -> IO.Path.GetFullPath p + (match tfm with Some f -> "#" + f | None -> "")
            | None -> (try IO.Path.GetFullPath t with _ -> t)

    let isUnpinnedNuget (target: string) =
        target.Trim().StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) && not (target.Contains '@')

    let inScope (scope: Scope) (a: LoadedAssembly) =
        match a.Reference with
        | None -> scope = AllReferences
        | Some r ->
            match scope with
            | AllReferences -> true
            | WithDependencies -> r.Role <> Framework
            | PrimaryOnly -> r.Role = Primary

    member _.Loader = loader

    /// Resolves the target (restoring/building via dotnet as needed) and loads its references into FCS.
    /// The result is reused only while the files it was built from are unchanged (checked with a stat per call).
    member _.Catalog(target: string) : Catalog =
        let key = normalise target
        let gate = gates.GetOrAdd(key, fun _ -> obj ())
        lock gate (fun () ->
            let current =
                match catalogs.TryGetValue key with
                | true, c ->
                    let expired = isUnpinnedNuget target && DateTime.UtcNow - c.Created > TimeSpan.FromHours 24.0
                    if not expired && Targets.fingerprint c.Resolved.Watch = c.Fingerprint then Some c else None
                | _ -> None
            match current with
            | Some c -> c
            | None ->
                let resolved = Targets.resolve target
                let set = loader.Load resolved.References
                let entities =
                    lazy
                        (set.Assemblies
                         |> List.map (fun a -> a.Name, Extract.entitiesOf a)
                         |> Map.ofList)
                let c =
                    { Resolved = resolved
                      Set = set
                      Entities = entities
                      Fingerprint = Targets.fingerprint resolved.Watch
                      Created = DateTime.UtcNow }
                catalogs.[key] <- c
                c)

    /// Warnings about the target itself (e.g. project not built), to show next to any result.
    member this.Notes(target: string) : string list = (this.Catalog target).Resolved.Warnings

    member _.ScopedAssemblies(c: Catalog, scope: Scope, assembly: string option) : LoadedAssembly list =
        match assembly with
        | Some name ->
            let hits =
                c.Set.Assemblies |> List.filter (fun a -> String.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            if hits.IsEmpty then
                failwithf "No referenced assembly named '%s'. Use refs to list them." name
            hits
        | None ->
            let s = c.Set.Assemblies |> List.filter (inScope scope)
            // a bare dll with nothing marked Primary should never yield an empty index
            if s.IsEmpty then c.Set.Assemblies else s

    member this.Entities(c: Catalog, scope: Scope, assembly: string option) : Entity list =
        let byAsm = c.Entities.Value
        [ for a in this.ScopedAssemblies(c, scope, assembly) do
            match byAsm.TryFind a.Name with
            | Some es -> yield! es
            | None -> () ]

    member this.Refs(target: string) : Catalog = this.Catalog target

    member this.Search(target: string, query: string, kind: string option, limit: int, scope: Scope) =
        let c = this.Catalog target
        Search.search (this.Entities(c, scope, None)) query kind limit

    /// Finds entities by full name (generic arity ignored, case-insensitive; unique suffix also accepted).
    member this.FindEntities(c: Catalog, name: string, scope: Scope) : Entity list =
        let n =
            let q = name.Trim()
            let q = (match q.IndexOf '<' with | -1 -> q | i -> q.Substring(0, i))
            (match q.IndexOf '`' with | -1 -> q | i -> q.Substring(0, i))
        let all = this.Entities(c, AllReferences, None)
        let ci = StringComparison.OrdinalIgnoreCase
        let exact = all |> List.filter (fun e -> String.Equals(e.FullName, n, ci))
        if not exact.IsEmpty then exact
        else all |> List.filter (fun e -> e.FullName.EndsWith("." + n, ci))

    /// Renders signatures for an entity name or a member of one (e.g. "System.String", "List.map").
    member this.Signature(target: string, name: string, docs: bool, ?maxEntities: int) : string option =
        let c = this.Catalog target
        let opts = { Renderer.RenderOptions.Docs = docs }
        let cap = defaultArg maxEntities 24
        let entities = this.FindEntities(c, name, AllReferences)
        if not entities.IsEmpty then
            // Several entities can share a name (an F# module and its type): show them all.
            let shown = entities |> List.truncate cap
            let hidden = entities.Length - shown.Length
            let text = shown |> List.map (Renderer.renderEntity opts) |> String.concat "\n\n"
            let note =
                if hidden > 0 then
                    sprintf "\n\n// %d more matching type(s) not shown; use a more specific, namespace-qualified name." hidden
                else ""
            Some(text + note)
        else
            match name.LastIndexOf '.' with
            | -1 -> None
            | i ->
                let owner, memberName = name.Substring(0, i), name.Substring(i + 1)
                let owners = this.FindEntities(c, owner, AllReferences)
                let rendered =
                    [ for e in owners do
                        let ms =
                            e.Members.Value
                            |> List.filter (fun m -> String.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase))
                        if not ms.IsEmpty then yield Renderer.renderEntityWith opts e ms true ]
                if rendered.IsEmpty then None
                else
                    let shown = rendered |> List.truncate cap
                    let hidden = rendered.Length - shown.Length
                    let note = if hidden > 0 then sprintf "\n\n// %d more matching type(s) not shown; use a more specific, namespace-qualified name." hidden else ""
                    Some(String.concat "\n\n" shown + note)

    member this.Outline(target: string, assembly: string option, scope: Scope) : string =
        let c = this.Catalog target
        let sb = System.Text.StringBuilder()
        for a in this.ScopedAssemblies(c, scope, assembly) do
            let es = c.Entities.Value |> Map.tryFind a.Name |> Option.defaultValue []
            sb.AppendLine(sprintf "// assembly: %s (%d public types and modules)" a.Name es.Length) |> ignore
            for g in es |> List.groupBy (fun e -> e.Namespace) |> List.sortBy fst do
                let ns, items = g
                sb.AppendLine(sprintf "namespace %s" (if ns = "" then "<global>" else ns)) |> ignore
                for e in items |> List.sortBy (fun e -> e.FullName) do
                    let nested =
                        let rel = if ns = "" then e.FullName else e.FullName.Substring(ns.Length + 1)
                        if rel.Contains "." then "  // nested" else ""
                    sb.AppendLine(sprintf "    %s%s" (Renderer.entitySummaryLine e) nested) |> ignore
            sb.AppendLine() |> ignore
        sb.ToString().TrimEnd()

    member this.Check(target: string, code: string) : Diagnostic list =
        let c = this.Catalog target
        Snippet.check loader c.Set code
