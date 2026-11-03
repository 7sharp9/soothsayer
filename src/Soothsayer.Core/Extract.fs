/// Walks an FSharpAssemblySignature and produces Entity/Member values with F# syntax signatures.
module Soothsayer.Core.Extract

open System
open System.Collections.Generic
open FSharp.Compiler.Symbols
open Soothsayer.Core.Loader
open Soothsayer.Core

let private dc = FSharpDisplayContext.Empty.WithShortTypeNames true

/// Visible marker for text FCS could not produce, so output never looks complete when it is not.
let private unavailable (ex: exn) =
    let first = (ex.Message.Split('\n').[0]).Trim()
    "(* unavailable: " + first.Replace("*)", "* )") + " *)"

let private fmtType (t: FSharpType) =
    try t.Format dc
    with ex ->
        // Types from unresolved references can throw while formatting; keep the rest of the signature.
        unavailable ex

let private stripAbbrev (t: FSharpType) = if t.IsAbbreviation then t.StripAbbreviations() else t

/// Type text in argument position: function and tuple types need parentheses.
let private argType (t: FSharpType) =
    let s = fmtType t
    let u = stripAbbrev t
    if (not t.IsAbbreviation) && (u.IsFunctionType || u.IsTupleType) then "(" + s + ")" else s

let private isUnit (t: FSharpType) = (stripAbbrev t).IsUnitType

let private isIdentifier (s: string) =
    s.Length > 0 && (Char.IsLetter s.[0] || s.[0] = '_') && s |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '\'')

/// Display name safe to put after val/member: operators in parens, odd names in double backticks.
let private declName (m: string) =
    if isIdentifier m then m
    elif m.StartsWith "(" then m
    elif m |> Seq.forall (fun c -> "!%&*+-./<=>?@^|~:".IndexOf c >= 0) then "( " + m + " )"
    else "``" + m + "``"

let private fsharpKeywords =
    set [ "abstract"; "and"; "as"; "assert"; "base"; "begin"; "class"; "const"; "default"; "delegate"; "do"; "done"; "downcast"
          "downto"; "elif"; "else"; "end"; "exception"; "extern"; "false"; "finally"; "fixed"; "for"; "fun"; "function"; "global"
          "if"; "in"; "inherit"; "inline"; "interface"; "internal"; "lazy"; "let"; "match"; "member"; "module"; "mutable"
          "namespace"; "new"; "null"; "of"; "open"; "or"; "override"; "private"; "public"; "rec"; "return"; "sig"
          "static"; "struct"; "then"; "to"; "true"; "try"; "type"; "upcast"; "use"; "val"; "void"; "when"; "while"; "with"; "yield" ]

let private escapeKeyword (n: string) = if fsharpKeywords.Contains n then "``" + n + "``" else n

let private paramText (p: FSharpParameter) =
    let name = p.Name |> Option.defaultValue "" |> escapeKeyword
    let ty =
        if p.IsOptionalArg && p.Type.IsFSharpOption && p.Type.GenericArguments.Count = 1 then
            argType p.Type.GenericArguments.[0]
        else argType p.Type
    let core =
        match name, p.IsOptionalArg with
        | "", false -> ty
        | "", true -> "?: " + ty
        | n, false -> n + ": " + ty
        | n, true -> "?" + n + ": " + ty
    let core = if p.IsOutArg then "[<Out>] " + core else core
    if p.IsParamArrayArg then "[<ParamArray>] " + core else core

let private groupText (g: IList<FSharpParameter>) =
    if g.Count = 0 then "unit"
    elif g.Count = 1 && g.[0].Name.IsNone && isUnit g.[0].Type then "unit"
    else g |> Seq.map paramText |> String.concat " * "

let private groupsText (groups: IList<IList<FSharpParameter>>) =
    if groups.Count = 0 then "" else groups |> Seq.map groupText |> String.concat " -> "

let private returnText (m: FSharpMemberOrFunctionOrValue) =
    try
        let r = m.ReturnParameter.Type
        if isUnit r then "unit" else fmtType r
    with ex -> unavailable ex

let private gpName (g: FSharpGenericParameter) =
    let n = g.Name.TrimStart('\'', '^')
    (if g.IsSolveAtCompileTime then "^" else "'") + n

let private constraintTexts (g: FSharpGenericParameter) : string list =
    let n = gpName g
    [ for c in g.Constraints do
        if c.IsEqualityConstraint then yield n + " : equality"
        elif c.IsComparisonConstraint then yield n + " : comparison"
        elif c.IsReferenceTypeConstraint then yield n + " : not struct"
        elif c.IsNonNullableValueTypeConstraint then yield n + " : struct"
        elif c.IsRequiresDefaultConstructorConstraint then yield n + " : (new: unit -> " + n + ")"
        elif c.IsSupportsNullConstraint then yield n + " : null"
        elif c.IsUnmanagedConstraint then yield n + " : unmanaged"
        elif c.IsCoercesToConstraint then yield n + " :> " + fmtType c.CoercesToTarget
        elif c.IsEnumConstraint then yield n + " : enum<" + fmtType c.EnumConstraintTarget + ">"
        elif c.IsMemberConstraint then
            let d = c.MemberConstraintData
            let args = d.MemberArgumentTypes |> Seq.map fmtType |> String.concat " * "
            yield
                sprintf "%s : (%smember %s: %s -> %s)" n (if d.MemberIsStatic then "static " else "") d.MemberName
                    (if args = "" then "unit" else args) (fmtType d.MemberReturnType) ]

let private whenClause (gps: IList<FSharpGenericParameter>) =
    let cs = [ for g in gps do yield! constraintTexts g ]
    if cs.IsEmpty then "" else " when " + String.concat " and " cs

let private genericArgs (gps: IList<FSharpGenericParameter>) =
    let names = gps |> Seq.filter (fun g -> not g.IsMeasure || true) |> Seq.map gpName |> Seq.toList
    if names.IsEmpty then "" else "<" + String.concat "," names + ">"

let private isPublic (a: FSharpAccessibility) = a.IsPublic

/// Like `safe` for text: the fallback names the reason.
let private safeText (f: unit -> string) =
    try f () with ex -> unavailable ex

let private safe (f: unit -> 'a) (fallback: 'a) =
    // FCS throws on some members whose types come from unresolved assemblies; one bad member must not hide the rest.
    try f () with _ -> fallback

let private isObsolete (info: _ option) = info.IsSome

let private docOf (docs: XmlDocs.DocFile) (sigText: string) = lazy (XmlDocs.tryFind docs sigText)

// ---------- members ----------

let private propertyType (m: FSharpMemberOrFunctionOrValue) =
    if m.HasGetterMethod then m.GetterMethod.ReturnParameter.Type
    elif m.HasSetterMethod then
        let g = m.SetterMethod.CurriedParameterGroups |> Seq.collect id |> Seq.toList
        (List.last g).Type
    else m.ReturnParameter.Type

let private propertyIndexParams (m: FSharpMemberOrFunctionOrValue) : FSharpParameter list =
    if m.HasGetterMethod then m.GetterMethod.CurriedParameterGroups |> Seq.collect id |> Seq.toList
    elif m.HasSetterMethod then
        let g = m.SetterMethod.CurriedParameterGroups |> Seq.collect id |> Seq.toList
        g |> List.take (max 0 (g.Length - 1))
    else []

let private entityTypeText (e: FSharpEntity) =
    let core = safe (fun () -> e.DisplayNameCore) e.DisplayName
    let gps = safe (fun () -> e.GenericParameters) (ResizeArray() :> IList<_>)
    core + (if gps.Count = 0 then "" else genericArgs gps)

let private memberKindAndSig (ent: FSharpEntity) (m: FSharpMemberOrFunctionOrValue) : MemberKind * string =
    let name = declName m.DisplayName
    let isStatic = not m.IsInstanceMember
    let when' = whenClause m.GenericParameters
    if ent.IsFSharpModule || (not m.IsMember && not m.IsConstructor) then
        let inl = if m.InlineAnnotation = FSharpInlineAnnotation.AlwaysInline then "inline " else ""
        if m.CurriedParameterGroups.Count > 0 then
            Function, sprintf "val %s%s: %s -> %s%s" inl name (groupsText m.CurriedParameterGroups) (returnText m) when'
        else
            let mut = if m.IsMutable then "mutable " else ""
            Value, sprintf "val %s%s%s: %s%s" mut inl name (fmtType m.FullType) when'
    elif m.IsConstructor then
        // ReturnParameter of a constructor is unit; the result is the declaring type with its generic arguments
        Ctor, sprintf "new: %s -> %s" (groupsText m.CurriedParameterGroups) (entityTypeText ent)
    elif m.IsEvent then
        let dt = safeText (fun () -> fmtType m.EventDelegateType)
        Event, sprintf "%s%s %s: %s" (if isStatic then "static " else "") "event" name dt
    elif m.IsProperty then
        // only public accessors are API: `{ get; internal set; }` is get-only to a consumer
        let pubGet = m.HasGetterMethod && safe (fun () -> isPublic m.GetterMethod.Accessibility) true
        let pubSet = m.HasSetterMethod && safe (fun () -> isPublic m.SetterMethod.Accessibility) true
        let accessors =
            match pubGet, pubSet with
            | true, true -> " with get, set"
            | false, true -> " with set"
            | _ -> " with get"
        let idx = propertyIndexParams m
        let ty = fmtType (propertyType m)
        let sigText =
            if idx.IsEmpty then ty else sprintf "%s -> %s" (idx |> List.map paramText |> String.concat " * ") ty
        let kw =
            if m.IsDispatchSlot then "abstract"
            elif isStatic then "static member"
            else "member"
        Property, sprintf "%s %s: %s%s" kw name sigText accessors
    else
        let kw =
            if m.IsDispatchSlot then "abstract"
            elif isStatic then "static member"
            else "member"
        let generics = if m.GenericParameters.Count > 0 then genericArgs m.GenericParameters else ""
        Method,
        sprintf "%s %s%s: %s -> %s%s" kw name generics (groupsText m.CurriedParameterGroups |> fun s -> if s = "" then "unit" else s) (returnText m) when'

let private skipMember (m: FSharpMemberOrFunctionOrValue) =
    safe
        (fun () ->
            not (isPublic m.Accessibility)
            || m.IsCompilerGenerated
            || m.IsExplicitlySuppressed
            || m.IsPropertyGetterMethod
            || m.IsPropertySetterMethod
            || m.IsEventAddMethod
            || m.IsEventRemoveMethod
            || m.IsUnionCaseTester
            || m.IsOverrideOrExplicitInterfaceImplementation
            || m.IsBaseValue
            || m.IsMemberThisValue
            || m.IsConstructorThisValue
            || m.DisplayName.StartsWith "get_" && m.IsMethod && m.IsPropertyGetterMethod)
        true

let private unionCaseText (uc: FSharpUnionCase) =
    let fields =
        uc.Fields
        |> Seq.map (fun f ->
            if f.IsNameGenerated then argType f.FieldType else f.Name + ": " + argType f.FieldType)
        |> String.concat " * "
    if fields = "" then "| " + uc.DisplayName else sprintf "| %s of %s" uc.DisplayName fields

let private fieldLiteral (f: FSharpField) =
    match f.LiteralValue with
    | Some v -> string v
    | None -> ""

let private entityMembers (docs: XmlDocs.DocFile) (fullName: string) (ent: FSharpEntity) (kind: EntityKind) : Member list =
    let mk name kind isStatic (sg: unit -> string) (xmlSig: string) =
        { Name = name
          Kind = kind
          FullName = fullName + "." + name
          IsStatic = isStatic
          Signature = lazy (safeText sg)
          Doc = docOf docs xmlSig }
    let cases =
        if kind = Union then
            [ for uc in safe (fun () -> Seq.toList ent.UnionCases) [] do
                if isPublic uc.Accessibility then
                    yield mk uc.DisplayName UnionCase true (fun () -> unionCaseText uc) uc.XmlDocSig ]
        else []
    let fields =
        match kind with
        | Union | Module | Delegate | Abbreviation | Measure | Interface -> []
        | _ ->
            [ for f in safe (fun () -> Seq.toList ent.FSharpFields) [] do
                let skip =
                    safe (fun () ->
                        not (isPublic f.Accessibility) || f.IsCompilerGenerated || f.Name = "value__" || f.Name.Contains "@" || f.Name.StartsWith "<") true
                if not skip then
                    let sg () =
                        match kind with
                        | Enum -> sprintf "| %s = %s" f.Name (fieldLiteral f)
                        | Record | Exception -> sprintf "%s%s: %s" (if f.IsMutable then "mutable " else "") f.DisplayName (fmtType f.FieldType)
                        | _ ->
                            let lit = if f.IsLiteral then sprintf " = %s" (fieldLiteral f) else ""
                            sprintf "%sval %s%s%s: %s%s" (if f.IsStatic then "static " else "") (if f.IsMutable then "mutable " else "") "" f.DisplayName (fmtType f.FieldType) lit
                    yield mk f.Name Field f.IsStatic sg f.XmlDocSig ]
    let members =
        let caseNames = if kind = Union then ent.UnionCases |> Seq.map (fun c -> c.Name) |> Set.ofSeq else Set.empty
        [ for m in safe (fun () -> Seq.toList ent.MembersFunctionsAndValues) [] do
            // union case factories (static member Some) and Tag duplicate the cases already listed
            let generatedUnionMember =
                kind = Union && safe (fun () -> (not m.IsInstanceMember && caseNames.Contains m.DisplayName) || m.DisplayName = "Tag" || m.DisplayName.StartsWith "New" && caseNames.Contains(m.DisplayName.Substring 3)) false
            if not generatedUnionMember && not (skipMember m) then
                let name = safe (fun () -> m.DisplayName) "?"
                let kindAndSig = lazy (memberKindAndSig ent m)
                let kind0 =
                    safe (fun () ->
                        if m.IsConstructor then Ctor
                        elif ent.IsFSharpModule || not m.IsMember then (if m.CurriedParameterGroups.Count > 0 then Function else Value)
                        elif m.IsEvent then Event
                        elif m.IsProperty then Property
                        else Method) Method
                yield
                    { Name = (if m.IsConstructor then "new" else name)
                      Kind = kind0
                      FullName = fullName + "." + (if m.IsConstructor then "new" else name)
                      IsStatic = not m.IsInstanceMember
                      Signature =
                        lazy
                            (let text = safeText (fun () -> snd kindAndSig.Value)
                             if safe (fun () -> isObsolete m.ObsoleteDiagnosticInfo) false then "[<Obsolete>] " + text else text)
                      Doc = docOf docs (safe (fun () -> m.XmlDocSig) "") } ]
    let kindOrder k =
        match k with
        | UnionCase -> 0
        | Ctor -> 1
        | Field -> 2
        | Property -> 3
        | Event -> 4
        | Value -> 5
        | Function | Method -> 6
    (cases @ fields @ members)
    |> List.sortBy (fun m -> kindOrder m.Kind, not m.IsStatic, m.Name)

// ---------- entities ----------

let private kindOf (e: FSharpEntity) =
    if e.IsFSharpModule then Some Module
    elif e.IsMeasure then Some Measure
    elif e.IsFSharpExceptionDeclaration then Some Exception
    elif e.IsFSharpAbbreviation then Some Abbreviation
    elif e.IsEnum then Some Enum
    elif e.IsDelegate then Some Delegate
    elif e.IsInterface then Some Interface
    elif e.IsFSharpRecord then Some Record
    elif e.IsFSharpUnion then Some Union
    elif e.IsValueType then Some Struct
    elif e.IsClass then Some Class
    // F# classes with a hidden representation (FSharpChecker) report IsClass = false and IsOpaque = true
    else Some Class

let private noisyInterfaces =
    set [ "IEquatable"; "IStructuralEquatable"; "IComparable"; "IStructuralComparable"; "IComparable<" ]

let private interfaceTexts (e: FSharpEntity) =
    safe
        (fun () ->
            e.DeclaredInterfaces
            |> Seq.map fmtType
            |> Seq.filter (fun s ->
                not (e.IsFSharp && (e.IsFSharpRecord || e.IsFSharpUnion || e.IsFSharpExceptionDeclaration)
                     && (s.StartsWith "System.IEquatable" || s.StartsWith "IEquatable" || s.StartsWith "System.Collections.IStructural"
                         || s.StartsWith "IStructural" || s.StartsWith "IComparable" || s.StartsWith "System.IComparable"))
                && s <> "System.Runtime.CompilerServices.IsReadOnlyAttribute")
            |> Seq.toList)
        []

let private baseText (e: FSharpEntity) (kind: EntityKind) =
    match kind with
    | Interface | Module | Enum | Delegate | Abbreviation | Measure -> None
    | _ ->
        safe
            (fun () ->
                match e.BaseType with
                | Some t ->
                    let s = fmtType t
                    match s with
                    | "obj" | "Object" | "System.Object" | "ValueType" | "System.ValueType" | "Enum" | "System.Enum" -> None
                    | s when (kind = Exception && (s = "exn" || s = "Exception")) -> None
                    | s -> Some s
                | None -> None)
            None

let private entityName (e: FSharpEntity) =
    let core = safe (fun () -> e.DisplayNameCore) e.DisplayName
    let gps = safe (fun () -> e.GenericParameters) (ResizeArray() :> IList<_>)
    if gps.Count = 0 then core
    else
        // F# puts type-level constraints inside the angle brackets: Set<'T when 'T : comparison>
        let names = gps |> Seq.map gpName |> String.concat ","
        core + "<" + names + safe (fun () -> whenClause gps) "" + ">"

let private extraText (e: FSharpEntity) kind =
    try
        (fun () ->
            match kind with
            | Abbreviation -> Some(fmtType e.AbbreviatedType)
            | Delegate ->
                // FSharpDelegateSignature throws for IL delegates such as Func<_,_>; the Invoke method has the same information.
                let viaSignature () =
                    let s = e.FSharpDelegateSignature
                    let args =
                        s.DelegateArguments
                        |> Seq.map (fun (n, t) -> (match n with | Some n -> escapeKeyword n + ": " | None -> "") + argType t)
                        |> String.concat " * "
                    let ret = if isUnit s.DelegateReturnType then "unit" else fmtType s.DelegateReturnType
                    sprintf "%s -> %s" (if args = "" then "unit" else args) ret
                let viaInvoke () =
                    let invoke = e.MembersFunctionsAndValues |> Seq.find (fun m -> m.CompiledName = "Invoke")
                    let ret = if isUnit invoke.ReturnParameter.Type then "unit" else fmtType invoke.ReturnParameter.Type
                    sprintf "%s -> %s" (groupsText invoke.CurriedParameterGroups) ret
                Some(try viaSignature () with _ -> (try viaInvoke () with ex2 -> unavailable ex2))
            | Exception ->
                let fs =
                    e.FSharpFields
                    |> Seq.filter (fun f -> isPublic f.Accessibility)
                    |> Seq.map (fun f -> if f.IsNameGenerated then argType f.FieldType else f.Name + ": " + argType f.FieldType)
                    |> String.concat " * "
                if fs = "" then None else Some fs
            | _ -> None) ()
    with ex -> Some(unavailable ex)

/// Namespace of an entity; for nested entities FCS reports none, so derive it from the enclosing top-level one.
let private namespaceOf (full: string) (ent: FSharpEntity) =
    match safe (fun () -> ent.Namespace) None with
    | Some n -> n
    | None ->
        // nested entity: walk up the declaring chain to the first one with a namespace
        let rec up (e: FSharpEntity) =
            match safe (fun () -> e.DeclaringEntity) None with
            | Some d -> (match safe (fun () -> d.Namespace) None with | Some n -> n | None -> up d)
            | None -> ""
        up ent

let private isVisibleName (n: string) = not (n.StartsWith "<" || n.Contains "@" || n.Contains "$")

/// All public entities of an assembly, flattened, with lazily computed members.
let entitiesOf (asm: LoadedAssembly) : Entity list =
    let result = ResizeArray<Entity>()
    let rec walk (path: string) (ent: FSharpEntity) =
        let ok = safe (fun () -> not ent.IsUnresolved && not ent.IsExplicitlySuppressed) false
        if ok then
            let core = safe (fun () -> ent.DisplayNameCore) ent.DisplayName
            if isVisibleName core then
                let full = if path = "" then core else path + "." + core
                if ent.IsNamespace then
                    for n in safe (fun () -> Seq.toList ent.NestedEntities) [] do walk full n
                elif isPublic ent.Accessibility then
                    match kindOf ent with
                    | None -> ()
                    | Some kind ->
                        let ns = namespaceOf full ent
                        result.Add
                            { Assembly = asm.Name
                              Namespace = ns
                              Name = entityName ent
                              FullName = full
                              Kind = kind
                              Doc = docOf asm.Docs (safe (fun () -> ent.XmlDocSig) "")
                              Base = baseText ent kind
                              Interfaces = interfaceTexts ent
                              Extra = extraText ent kind
                              IsStruct = safe (fun () -> ent.IsValueType && not ent.IsEnum) false
                              IsObsolete = safe (fun () -> isObsolete ent.ObsoleteDiagnosticInfo) false
                              Members = lazy (entityMembers asm.Docs full ent kind) }
                        for n in safe (fun () -> Seq.toList ent.NestedEntities) [] do walk full n
    // FCS lists top-level types and modules flat, each carrying its namespace; only nested ones live under a parent.
    for e in safe (fun () -> Seq.toList asm.Signature.Entities) [] do
        let ns = safe (fun () -> match e.Namespace with Some n -> n | None -> "") ""
        walk ns e
    List.ofSeq result
