/// Reads the .xml documentation file next to a dll and returns plain-text summaries keyed by XmlDocSig.
module Soothsayer.Core.XmlDocs

open System
open System.IO
open System.Text
open System.Xml.Linq
open System.Collections.Generic

let private collapse (s: string) =
    let sb = StringBuilder()
    let mutable lastSpace = true
    for c in s do
        if Char.IsWhiteSpace c then
            if not lastSpace then sb.Append ' ' |> ignore
            lastSpace <- true
        else
            sb.Append c |> ignore
            lastSpace <- false
    sb.ToString().Trim()

/// Last identifier of a cref such as "M:System.String.Split(System.Char)" -> "String.Split".
let private crefText (cref: string) =
    if String.IsNullOrEmpty cref then ""
    else
        let body = if cref.Length > 2 && cref.[1] = ':' then cref.Substring 2 else cref
        let body = (match body.IndexOf '(' with | -1 -> body | i -> body.Substring(0, i))
        let parts = body.Split('.')
        let take = if cref.StartsWith "T:" || parts.Length < 2 then 1 else 2
        let tail = parts |> Array.skip (max 0 (parts.Length - take))
        String.Join(".", tail).Replace("`", "")

let rec private textOf (n: XNode) : string =
    match n with
    | :? XText as t -> t.Value
    | :? XElement as e ->
        match e.Name.LocalName with
        | "see" | "seealso" ->
            match e.Attribute(XName.Get "cref"), e.Attribute(XName.Get "langword"), e.Attribute(XName.Get "href") with
            | null, null, null -> collapse e.Value
            | c, null, _ when not (isNull c) -> crefText c.Value
            | _, l, _ when not (isNull l) -> l.Value
            | _, _, h -> h.Value
        | "paramref" | "typeparamref" ->
            match e.Attribute(XName.Get "name") with
            | null -> ""
            | a -> a.Value
        | "para" | "br" -> " " + (e.Nodes() |> Seq.map textOf |> String.concat "") + " "
        | _ -> e.Nodes() |> Seq.map textOf |> String.concat ""
    | _ -> ""

type DocFile =
    { Summaries: IReadOnlyDictionary<string, string> }

let private cache = Dictionary<string, DocFile>(StringComparer.Ordinal)

let private loadXml (xmlPath: string) : DocFile =
    let d = Dictionary<string, string>(StringComparer.Ordinal)
    try
        let doc = XDocument.Load xmlPath
        for m in doc.Descendants(XName.Get "member") do
            match m.Attribute(XName.Get "name") with
            | null -> ()
            | a ->
                let summary = m.Element(XName.Get "summary")
                if not (isNull summary) then
                    let text = summary.Nodes() |> Seq.map textOf |> String.concat "" |> collapse
                    if text <> "" then d.[a.Value] <- text
    with ex ->
        // A malformed or unreadable doc file only costs us the docs, not the signatures.
        eprintfn "soothsayer: could not read %s: %s" xmlPath ex.Message
    { Summaries = d }

/// Doc file for a dll path, or an empty set when there is no sibling .xml.
let forDll (dllPath: string) : DocFile =
    lock cache (fun () ->
        match cache.TryGetValue dllPath with
        | true, d -> d
        | _ ->
            let xml = Path.ChangeExtension(dllPath, ".xml")
            let d = if File.Exists xml then loadXml xml else { Summaries = Dictionary() }
            cache.[dllPath] <- d
            d)

let tryFind (docs: DocFile) (xmlDocSig: string) : string option =
    if String.IsNullOrEmpty xmlDocSig then None
    else
        match docs.Summaries.TryGetValue xmlDocSig with
        | true, s -> Some s
        | _ -> None
