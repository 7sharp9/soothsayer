/// Resolves a target string (project, dll, or nuget:Id@version) to a list of reference assemblies.
module Soothsayer.Core.Targets

open System
open System.IO
open System.Diagnostics
open System.Reflection
open System.Runtime.InteropServices
open System.Security
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

let private dotnetExe () =
    match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
    | null
    | "" -> "dotnet"
    | p -> p

/// Runs a process and returns (exit code, stdout, stderr). Nothing is written to our own stdout.
/// A child that runs past `timeout` is killed together with its process tree and an exception is raised.
let runProcess (exe: string) (args: string list) (workDir: string) (timeout: TimeSpan) =
    let psi = ProcessStartInfo(exe)

    for a in args do
        psi.ArgumentList.Add a

    psi.WorkingDirectory <- workDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.RedirectStandardInput <- true
    psi.UseShellExecute <- false
    // Keep MSBuild out of node-reuse so the tool exits promptly.
    psi.Environment.["MSBUILDDISABLENODEREUSE"] <- "1"
    psi.Environment.["DOTNET_NOLOGO"] <- "1"
    psi.Environment.["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
    use p = new Process(StartInfo = psi)
    p.Start() |> ignore
    p.StandardInput.Close()
    let outT = p.StandardOutput.ReadToEndAsync()
    let errT = p.StandardError.ReadToEndAsync()

    if not (p.WaitForExit(int timeout.TotalMilliseconds)) then
        // the process may exit between the timeout and the kill; either way it is gone afterwards
        (try
            p.Kill true
         with _ ->
             ())

        failwithf
            "'%s %s' did not finish within %.0f seconds and was killed."
            exe
            (String.Join(" ", args))
            timeout.TotalSeconds

    p.WaitForExit()
    p.ExitCode, outT.Result, errT.Result

let private processTimeout = TimeSpan.FromMinutes 5.0

let private run exe args workDir =
    runProcess exe args workDir processTimeout

let private isManaged (path: string) =
    try
        AssemblyName.GetAssemblyName path |> ignore
        true
    with _ ->
        // BadImageFormat for native libraries sitting next to managed ones; they are not references.
        false

let private simpleName (path: string) = Path.GetFileNameWithoutExtension path

/// Reference assemblies of the newest installed Microsoft.NETCore.App.Ref pack; falls back to the running runtime.
let frameworkReferenceDlls () : string list =
    let rtDir = RuntimeEnvironment.GetRuntimeDirectory()

    let dotnetRoot =
        DirectoryInfo(rtDir.TrimEnd(Path.DirectorySeparatorChar)).Parent.Parent.Parent.FullName

    let packs = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref")

    let fromPack =
        if Directory.Exists packs then
            Directory.GetDirectories packs
            |> Array.choose (fun d ->
                match Version.TryParse(Path.GetFileName d) with
                | true, v ->
                    let refDir =
                        Directory.GetDirectories(Path.Combine(d, "ref"), "net*")
                        |> Array.sortDescending
                        |> Array.tryHead

                    refDir |> Option.map (fun r -> v, r)
                | _ -> None)
            |> Array.sortByDescending fst
            |> Array.tryHead
        else
            None

    match fromPack with
    | Some(_, dir) -> Directory.GetFiles(dir, "*.dll") |> Array.toList
    | None -> Directory.GetFiles(rtDir, "*.dll") |> Array.filter isManaged |> Array.toList

/// FSharp.Core that ships with this tool; used when the target does not bring its own.
let private ownFSharpCore () =
    typeof<Microsoft.FSharp.Core.Unit>.Assembly.Location

let private mkRef role pkgId pkgVer path =
    {
        Path = path
        Name = simpleName path
        Role = role
        PackageId = pkgId
        PackageVersion = pkgVer
    }

let private ensureFSharpCore (refs: Reference list) =
    if refs |> List.exists (fun r -> r.Name = "FSharp.Core") then
        refs
    else
        refs @ [ mkRef Dependency (Some "FSharp.Core") None (ownFSharpCore ()) ]

let private dedupe (refs: Reference list) =
    // First wins: callers put the most important reference first.
    refs |> List.distinctBy (fun r -> r.Name.ToLowerInvariant())

// ---------- target string syntax ----------

let private projectTarget =
    Regex(
        @"^(?<path>.+\.(?:cs|fs|vb)proj)(?:[#@](?<tfm>[^#@/\\]+))?$",
        RegexOptions.IgnoreCase ||| RegexOptions.Compiled
    )

/// Splits "path/App.fsproj#net8.0" (or "@net8.0") into the project path and an optional target framework.
let splitProjectTarget (target: string) : (string * string option) option =
    let m = projectTarget.Match(target.Trim())

    if m.Success then
        Some(
            m.Groups.["path"].Value,
            (if m.Groups.["tfm"].Success then
                 Some m.Groups.["tfm"].Value
             else
                 None)
        )
    else
        None

// ---------- msbuild ----------

let private jsonProp (root: JsonElement) (name: string) =
    match root.TryGetProperty "Properties" with
    | true, p ->
        match p.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String && v.GetString() <> "" -> Some(v.GetString())
        | _ -> None
    | _ -> None

let private parseJsonOutput (what: string) (out: string) =
    // -getItem/-getProperty output is JSON; skip any non-JSON preamble.
    let start = out.IndexOf '{'

    if start < 0 then
        failwithf "dotnet msbuild produced no JSON for %s:\n%s" what out

    JsonDocument.Parse(out.Substring start)

/// Parses `-getItem:ReferencePath` JSON output.
let private parseReferencePath (root: JsonElement) (primaryPackage: string option) : Reference list =
    let items = root.GetProperty("Items").GetProperty("ReferencePath")

    let str (e: JsonElement) (n: string) =
        match e.TryGetProperty n with
        | true, v when v.ValueKind = JsonValueKind.String && v.GetString() <> "" -> Some(v.GetString())
        | _ -> None

    [
        for it in items.EnumerateArray() do
            match str it "FullPath" |> Option.orElse (str it "Identity") with
            | None -> ()
            | Some path ->
                let pkg = str it "NuGetPackageId"
                let ver = str it "NuGetPackageVersion"

                let isFramework =
                    (str it "FrameworkReferenceName").IsSome
                    || path.Contains(Path.Combine("packs", "Microsoft.NETCore.App.Ref"))

                let role =
                    if isFramework then
                        Framework
                    else
                        match primaryPackage, pkg with
                        | Some p, Some q when String.Equals(p, q, StringComparison.OrdinalIgnoreCase) -> Primary
                        | Some _, _ -> Dependency
                        | None, _ -> Primary

                yield mkRef role pkg ver path
    ]

/// Picks the highest plain netX.Y; then netX.Y-platform; then netcoreapp/netstandard; then anything.
let chooseTargetFramework (tfms: string list) : string option =
    let rank (t: string) =
        let m = Regex.Match(t, @"^net(\d+)\.(\d+)(-.+)?$")

        if m.Success then
            (if m.Groups.[3].Success then 2 else 3), Version(int m.Groups.[1].Value, int m.Groups.[2].Value)
        else
            let m2 = Regex.Match(t, @"^(?:netcoreapp|netstandard)(\d+)\.(\d+)$")

            if m2.Success then
                1, Version(int m2.Groups.[1].Value, int m2.Groups.[2].Value)
            else
                0, Version(0, 0)

    match tfms with
    | [] -> None
    | l -> l |> List.maxBy rank |> Some

type private MsbuildResult =
    {
        Refs: Reference list
        TargetPath: string option
        AssetsFile: string option
        Tfm: string option
    }

let private msbuildReferences
    (project: string)
    (workDir: string)
    (primaryPackage: string option)
    (requestedTfm: string option)
    =
    // 1. Which frameworks does the project target? A multi-target project fails ResolveAssemblyReferences (MSB4057)
    //    unless one TargetFramework is chosen.
    let tfm =
        match requestedTfm with
        | Some t -> Some t
        | None ->
            let code, out, err =
                run
                    (dotnetExe ())
                    [
                        "msbuild"
                        project
                        "-nologo"
                        "-getProperty:TargetFramework,TargetFrameworks"
                    ]
                    workDir

            if code <> 0 then
                failwithf "dotnet msbuild could not evaluate %s (exit %d):\n%s" project code ((out + "\n" + err).Trim())

            use doc = parseJsonOutput project out

            match jsonProp doc.RootElement "TargetFramework" with
            | Some _ -> None // single target: nothing to choose
            | None ->
                match jsonProp doc.RootElement "TargetFrameworks" with
                | Some many ->
                    chooseTargetFramework (
                        many.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                        |> Array.toList
                    )
                | None -> None

    let args =
        [
            yield "msbuild"
            yield project
            yield "-restore"
            yield "-nologo"
            yield "-v:q"
            yield "-t:ResolveAssemblyReferences"
            yield "-p:DesignTimeBuild=true"
            yield "-p:BuildProjectReferences=false"
            match tfm with
            | Some t -> yield "-p:TargetFramework=" + t
            | None -> ()
            yield "-getItem:ReferencePath"
            yield "-getProperty:TargetPath,ProjectAssetsFile,TargetFramework"
        ]

    let code, out, err = run (dotnetExe ()) args workDir

    if code <> 0 then
        failwithf "dotnet msbuild failed (exit %d) for %s:\n%s" code project ((out + "\n" + err).Trim())

    use doc = parseJsonOutput project out

    {
        Refs = parseReferencePath doc.RootElement primaryPackage
        TargetPath = jsonProp doc.RootElement "TargetPath"
        AssetsFile = jsonProp doc.RootElement "ProjectAssetsFile"
        Tfm = tfm |> Option.orElse (jsonProp doc.RootElement "TargetFramework")
    }

let private cacheRoot () =
    match Environment.GetEnvironmentVariable "SOOTHSAYER_CACHE" with
    | null
    | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".cache", "soothsayer")
    | p -> p

let private statStamp (path: string) =
    let i = FileInfo path

    if i.Exists then
        sprintf "%s|%d|%d" path i.Length i.LastWriteTimeUtc.Ticks
    else
        path + "|missing"

/// Cheap change detector: size and mtime of every file whose change should invalidate a resolved target.
let fingerprint (watch: string list) : string =
    let sb = StringBuilder()

    for w in watch do
        sb.AppendLine(statStamp w) |> ignore

    sb.ToString()

let private sourceExtensions = set [ ".fs"; ".cs"; ".vb" ]

let private newestSource (projectDir: string) : DateTime option =
    let skip (p: string) =
        let sep = string Path.DirectorySeparatorChar
        p.Contains(sep + "obj" + sep) || p.Contains(sep + "bin" + sep)

    try
        Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories)
        |> Seq.filter (fun p ->
            sourceExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())
            && not (skip p))
        |> Seq.map File.GetLastWriteTimeUtc
        |> Seq.fold
            (fun (acc: DateTime option) t ->
                match acc with
                | Some a when a >= t -> acc
                | _ -> Some t)
            None
    with _ ->
        // unreadable directory: the staleness hint is best effort
        None

let private fromProject (target: string) : ResolvedTarget =
    let path, tfm =
        match splitProjectTarget target with
        | Some(p, t) -> p, t
        | None -> target, None

    let full = Path.GetFullPath path

    if not (File.Exists full) then
        failwithf "Project not found: %s" full

    let dir = Path.GetDirectoryName full
    let r = msbuildReferences full dir None tfm

    let own, warnings =
        match r.TargetPath with
        | Some t when File.Exists t ->
            let stale =
                match newestSource dir with
                | Some newest when newest > File.GetLastWriteTimeUtc t ->
                    [
                        sprintf "The project's own output %s is older than its sources: " t
                        + "run 'dotnet build' first or its newest API will be missing."
                    ]
                | _ -> []

            [ mkRef Primary None None t ], stale
        | Some t ->
            [],
            [
                sprintf "The project's own output %s does not exist: run 'dotnet build' first, " t
                + "otherwise only its references are visible, not the project's own types."
            ]
        | None ->
            [],
            [
                "Could not determine the project's output path; only its references are visible."
            ]

    let refs = own @ r.Refs |> ensureFSharpCore |> dedupe

    {
        Target = target
        Description = sprintf "project %s (%s)" full (defaultArg r.Tfm "single target")
        References = refs
        Warnings = warnings
        Watch =
            [
                yield full
                match r.AssetsFile with
                | Some a -> yield a
                | None -> ()
                match r.TargetPath with
                | Some t -> yield t
                | None -> ()
                yield! refs |> List.map (fun x -> x.Path)
            ]
    }

let private fromDll (path: string) : ResolvedTarget =
    let full = Path.GetFullPath path

    if not (File.Exists full) then
        failwithf "dll not found: %s" full

    let dir = Path.GetDirectoryName full
    let framework = frameworkReferenceDlls () |> List.map (mkRef Framework None None)

    let siblings =
        Directory.GetFiles(dir, "*.dll")
        |> Array.filter (fun p -> p <> full && isManaged p)
        |> Array.toList
        |> List.map (mkRef Dependency None None)
    // Target first, then framework reference pack (so a publish folder's implementation assemblies do not shadow it).
    let refs =
        [ mkRef Primary None None full ] @ framework @ siblings
        |> ensureFSharpCore
        |> dedupe

    {
        Target = path
        Description = sprintf "dll %s (+ siblings, framework reference pack)" full
        References = refs
        Warnings = []
        Watch = [ full ] @ (siblings |> List.map (fun s -> s.Path))
    }

let private safeName (s: string) =
    String(
        s
        |> Seq.map (fun c ->
            if Char.IsLetterOrDigit c || c = '.' || c = '-' || c = '_' || c = '@' then
                c
            else
                '_')
        |> Seq.toArray
    )

let private serializeRefs (refs: Reference list) =
    refs
    |> List.map (fun r ->
        let role =
            match r.Role with
            | Primary -> "P"
            | Dependency -> "D"
            | Framework -> "F"

        String.Join("\t", [ role; defaultArg r.PackageId ""; defaultArg r.PackageVersion ""; r.Path ]))
    |> String.concat "\n"

let private deserializeRefs (text: string) =
    text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun l ->
        let p = l.Split('\t')
        let opt (s: string) = if s = "" then None else Some s

        let role =
            match p.[0] with
            | "P" -> Primary
            | "F" -> Framework
            | _ -> Dependency

        mkRef role (opt p.[1]) (opt p.[2]) p.[3])
    |> Array.toList

let private restoreProject =
    String.Join(
        "\n",
        [
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
            "  <PropertyGroup>"
            "    <TargetFramework>net10.0</TargetFramework>"
            "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
            "    <NuGetAudit>false</NuGetAudit>"
            "  </PropertyGroup>"
            "  <ItemGroup>"
            "    <PackageReference Include=\"__ID__\" Version=\"__VER__\" />"
            "  </ItemGroup>"
            "</Project>"
        ]
    )

let private idPattern = Regex(@"^[A-Za-z0-9_.-]+$", RegexOptions.Compiled)

/// NuGet versions and ranges: digits, letters (prerelease labels), . + - * , ( ) [ ] and spaces.
/// No quotes or angle brackets.
let private versionPattern =
    Regex(@"^[A-Za-z0-9.+*,()\[\] -]+$", RegexOptions.Compiled)

/// Validates a nuget: target. The id and version end up in a generated project file, so anything that could
/// change the XML or MSBuild semantics is refused before any file is written.
let parseNugetTarget (spec: string) : string * string option =
    let body = spec.Trim().Substring "nuget:".Length

    let id, ver =
        match body.IndexOf '@' with
        | -1 -> body.Trim(), None
        | i -> body.Substring(0, i).Trim(), Some(body.Substring(i + 1).Trim())

    if not (idPattern.IsMatch id) then
        failwith (
            sprintf "Invalid NuGet package id '%s': " id
            + "use letters, digits, '.', '_' and '-' only (e.g. nuget:Newtonsoft.Json@13.0.3)."
        )

    match ver with
    | Some v when v <> "" && v <> "latest" && not (versionPattern.IsMatch v) ->
        failwithf "Invalid NuGet version '%s': use a version such as 1.2.3 or a range such as [1.0,2.0)." v
    | _ -> ()

    id, ver |> Option.filter (fun v -> v <> "" && v <> "latest")

let private fromNuget (spec: string) : ResolvedTarget =
    let id, ver = parseNugetTarget spec

    let dir =
        Path.Combine(cacheRoot (), "nuget", safeName (id + "@" + defaultArg ver "latest"))

    Directory.CreateDirectory dir |> ignore
    let cacheFile = Path.Combine(dir, "refs.json")

    let describe refs =
        let ver =
            refs
            |> List.tryPick (fun r ->
                match r.PackageId with
                | Some p when String.Equals(p, id, StringComparison.OrdinalIgnoreCase) -> r.PackageVersion
                | _ -> None)

        sprintf "NuGet package %s %s" id (defaultArg ver "(version unknown)")

    let tryCache () =
        if File.Exists cacheFile then
            try
                let age = DateTime.UtcNow - File.GetLastWriteTimeUtc cacheFile
                // an unpinned request means "latest"; re-resolve daily
                let fresh = ver.IsSome || age < TimeSpan.FromHours 24.0

                if not fresh then
                    None
                else
                    let refs = deserializeRefs (File.ReadAllText cacheFile)

                    if not refs.IsEmpty && refs |> List.forall (fun r -> File.Exists r.Path) then
                        Some refs
                    else
                        None
            with _ ->
                // corrupt cache file: fall through to a fresh restore
                None
        else
            None

    let refs =
        match tryCache () with
        | Some r -> r
        | None ->
            let versionAttr =
                match ver with
                | Some v when v.StartsWith "[" || v.StartsWith "(" -> v
                | Some v -> sprintf "[%s]" v
                | None -> "*"

            let proj = Path.Combine(dir, "restore.csproj")
            // validated above; escaping as well so a future relaxation of the patterns cannot reopen the hole
            File.WriteAllText(
                proj,
                restoreProject
                    .Replace("__ID__", SecurityElement.Escape id)
                    .Replace("__VER__", SecurityElement.Escape versionAttr)
            )

            let r = msbuildReferences proj dir (Some id) None
            let refs = r.Refs |> ensureFSharpCore |> dedupe
            File.WriteAllText(cacheFile, serializeRefs refs)
            refs

    if not (refs |> List.exists (fun r -> r.Role = Primary)) then
        failwithf
            "Package %s restored but contributed no compile-time references (it may be a tooling or analyzer package)."
            id

    {
        Target = spec
        Description = describe refs
        References = refs
        Warnings = []
        Watch = cacheFile :: (refs |> List.map (fun r -> r.Path))
    }

let resolve (target: string) : ResolvedTarget =
    let t = target.Trim()

    if t.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) then
        fromNuget t
    else
        match splitProjectTarget t with
        | Some _ -> fromProject t
        | None ->
            match Path.GetExtension(t).ToLowerInvariant() with
            | ".vbproj"
            | ".csproj"
            | ".fsproj" -> fromProject t
            | ".dll" -> fromDll t
            | _ ->
                failwith (
                    sprintf "Unrecognised target '%s'. " target
                    + "Use a .fsproj/.csproj path (optionally with #net8.0 to pick a target framework), "
                    + "a .dll path, or nuget:Package.Id@version (version optional)."
                )
