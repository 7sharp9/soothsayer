module Soothsayer.Core.Renderer

open System
open System.Text
open Soothsayer.Core

type RenderOptions =
    { Docs: bool }

let defaultOptions = { Docs = true }

/// "type Dictionary<'TKey,'TValue>", "module List", "exception MyError of string".
let entityHeader (e: Entity) =
    match e.Kind with
    | Module -> "module " + e.Name
    | Exception -> "exception " + e.Name + (match e.Extra with Some x -> " of " + x | None -> "")
    | Measure -> "[<Measure>] type " + e.Name
    | _ -> "type " + e.Name

let private docLines (indent: string) (doc: string option) =
    match doc with
    | Some d when d.Trim() <> "" -> [ indent + "/// " + d ]
    | _ -> []

let renderMember (opts: RenderOptions) (indent: string) (m: Member) : string list =
    (if opts.Docs then docLines indent m.Doc.Value else []) @ [ indent + m.Signature.Value ]

/// Renders an entity in .fsi style. `members` lets callers show only some members (e.g. a lookup of one method).
let renderEntityWith (opts: RenderOptions) (e: Entity) (members: Member list) (partial: bool) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.AppendLine s |> ignore
    line (sprintf "// assembly: %s" e.Assembly)
    let nsPrefix = if e.Namespace <> "" then e.Namespace else ""
    // Nested entities (type inside module/type) are shown with their full path as the header name.
    let nestedPath =
        if e.FullName.Length > nsPrefix.Length && nsPrefix <> "" then e.FullName.Substring(nsPrefix.Length + 1)
        else e.FullName
    if nsPrefix <> "" then line (sprintf "namespace %s" nsPrefix)
    if opts.Docs then
        for l in docLines "" e.Doc.Value do line l
    if e.IsObsolete then line "[<System.Obsolete>]"
    if e.IsStruct then line "[<Struct>]"
    let header =
        let h = entityHeader e
        if nestedPath <> e.Name && nestedPath.Contains "." then
            // show where a nested entity lives
            sprintf "%s  // nested in %s" h (nestedPath.Substring(0, nestedPath.LastIndexOf '.'))
        else h
    let body = ResizeArray<string>()
    match e.Kind with
    | _ when partial -> ()
    | Abbreviation -> ()
    | Delegate -> ()
    | _ ->
        match e.Base with
        | Some b -> body.Add(sprintf "    %s %s" (if e.Kind = Interface then "inherit" else "inherit") b)
        | None -> ()
        for i in e.Interfaces do
            body.Add(sprintf "    %s %s" (if e.Kind = Interface then "inherit" else "interface") i)
    let memberLines =
        [ for m in members do
            let lines = renderMember opts "    " m
            match e.Kind, m.Kind with
            | Record, Field ->
                // wrapped in braces below
                yield! lines
            | _ -> yield! lines ]
    match e.Kind with
    | Abbreviation -> line (sprintf "%s = %s" header (defaultArg e.Extra "(* unavailable *)"))
    | Delegate -> line (sprintf "%s = delegate of %s" header (defaultArg e.Extra "(* unavailable *)"))
    | Exception -> line header
    | Record ->
        line (header + " =")
        let fields = members |> List.filter (fun m -> m.Kind = Field)
        let others = members |> List.filter (fun m -> m.Kind <> Field)
        line "    {"
        for f in fields do
            for l in renderMember opts "      " f do line l
        line "    }"
        for b in body do line b
        for m in others do
            for l in renderMember opts "    " m do line l
    | _ ->
        if body.Count = 0 && memberLines.IsEmpty then line (header + " =" + (if e.Kind = Module then "" else " class end"))
        else
            line (header + " =")
            for b in body do line b
            for l in memberLines do line l
    if partial then line "    // (showing matching members only)"
    sb.ToString().TrimEnd()

let renderEntity (opts: RenderOptions) (e: Entity) : string =
    renderEntityWith opts e e.Members.Value false

/// One-line description used in search results and outlines.
let entitySummaryLine (e: Entity) =
    match e.Kind with
    | Module | Exception | Measure -> entityHeader e
    | Abbreviation -> sprintf "%s = %s" (entityHeader e) (defaultArg e.Extra "(* unavailable *)")
    | _ -> sprintf "%s [%s]" (entityHeader e) e.Kind.Label
