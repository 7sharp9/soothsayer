module Soothsayer.Core.Search

open System
open Soothsayer.Core

/// True when every character of `q` occurs in `s` in order.
let private isSubsequence (q: string) (s: string) =
    let mutable i = 0

    for c in s do
        if i < q.Length && q.[i] = c then
            i <- i + 1

    i = q.Length

/// Higher is better; 0 means no match.
let score (query: string) (fullName: string) : int =
    let q = query.Trim().ToLowerInvariant()
    let f = fullName.ToLowerInvariant()

    if q = "" then
        0
    else
        let lastSeg =
            (match f.LastIndexOf '.' with
             | -1 -> f
             | i -> f.Substring(i + 1))

        let lenPenalty = min 99 (f.Length / 4)

        if f = q then
            1000
        elif f.EndsWith("." + q) then
            900 - lenPenalty
        elif lastSeg = q then
            880 - lenPenalty
        elif lastSeg.StartsWith q then
            800 - lenPenalty
        elif f.Contains("." + q) then
            700 - lenPenalty
        elif f.Contains q then
            600 - lenPenalty
        else
            // fuzzy: query characters in order inside the last two segments
            let parts = f.Split('.')
            let tail = String.Join(".", parts |> Array.skip (max 0 (parts.Length - 2)))

            if q.Length >= 3 && isSubsequence (q.Replace(".", "")) (tail.Replace(".", "")) then
                200 - lenPenalty
            else
                0

let private kindMatches (kind: string option) (entityKind: EntityKind option) (memberKind: MemberKind option) =
    match kind with
    | None -> true
    | Some k ->
        (match entityKind with
         | Some ek -> ek.Label = k.ToLowerInvariant().Trim()
         | None -> false)
        || (match memberKind with
            | Some mk ->
                mk.Label = k.ToLowerInvariant().Trim()
                || (k.ToLowerInvariant() = "member"
                    && (mk = Method || mk = Property || mk = Ctor || mk = Event))
            | None -> false)

/// Case-insensitive match over entity full names and member full names.
let search (entities: Entity seq) (query: string) (kind: string option) (limit: int) : SearchHit list =
    // F# source queries often carry generic arity such as List`1 or <'T>; the index does not.
    let query =
        let q = query.Trim()

        let q =
            (match q.IndexOf '<' with
             | -1 -> q
             | i -> q.Substring(0, i))

        (match q.IndexOf '`' with
         | -1 -> q
         | i -> q.Substring(0, i))

    let hits = ResizeArray<int * string * SearchHit>()

    for e in entities do
        let es = score query e.FullName

        if es > 0 && kindMatches kind (Some e.Kind) None then
            hits.Add(
                es,
                e.FullName,
                {
                    Assembly = e.Assembly
                    FullName = e.FullName
                    Kind = e.Kind.Label
                    Signature = Renderer.entityHeader e
                    Doc = e.Doc.Value
                    Score = es
                }
            )
        // only walk members when the entity could contribute: avoids forcing member lists for unrelated types
        // when the query names a single segment we still need them, so always check
        for m in e.Members.Value do
            let ms = score query m.FullName

            if ms > 0 && kindMatches kind None (Some m.Kind) then
                hits.Add(
                    ms,
                    m.FullName,
                    {
                        Assembly = e.Assembly
                        FullName = m.FullName
                        Kind = m.Kind.Label
                        Signature = m.Signature.Value
                        Doc = m.Doc.Value
                        Score = ms
                    }
                )

    hits
    |> Seq.sortBy (fun (s, n, _) -> -s, n.Length, n)
    |> Seq.map (fun (_, _, h) -> h)
    |> Seq.truncate (max 1 limit)
    |> Seq.toList
