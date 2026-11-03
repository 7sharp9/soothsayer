namespace Soothsayer.Core

open System

type EntityKind =
    | Class
    | Record
    | Union
    | Struct
    | Enum
    | Interface
    | Delegate
    | Abbreviation
    | Exception
    | Measure
    | Module

    member k.Label =
        match k with
        | Class -> "class"
        | Record -> "record"
        | Union -> "union"
        | Struct -> "struct"
        | Enum -> "enum"
        | Interface -> "interface"
        | Delegate -> "delegate"
        | Abbreviation -> "abbreviation"
        | Exception -> "exception"
        | Measure -> "measure"
        | Module -> "module"

    static member TryParse(s: string) =
        match (if isNull s then "" else s.Trim().ToLowerInvariant()) with
        | "class" -> Some Class
        | "record" -> Some Record
        | "union" -> Some Union
        | "struct" -> Some Struct
        | "enum" -> Some Enum
        | "interface" -> Some Interface
        | "delegate" -> Some Delegate
        | "abbreviation"
        | "abbrev" -> Some Abbreviation
        | "exception" -> Some Exception
        | "measure" -> Some Measure
        | "module" -> Some Module
        | _ -> None

type MemberKind =
    | Ctor
    | Property
    | Method
    | Function
    | Value
    | UnionCase
    | Field
    | Event

    member k.Label =
        match k with
        | Ctor -> "ctor"
        | Property -> "property"
        | Method -> "method"
        | Function -> "function"
        | Value -> "value"
        | UnionCase -> "case"
        | Field -> "field"
        | Event -> "event"

    static member TryParse(s: string) =
        match (if isNull s then "" else s.Trim().ToLowerInvariant()) with
        | "ctor"
        | "constructor" -> Some Ctor
        | "property"
        | "prop" -> Some Property
        | "method" -> Some Method
        | "function"
        | "func" -> Some Function
        | "value"
        | "val" -> Some Value
        | "case"
        | "unioncase" -> Some UnionCase
        | "field" -> Some Field
        | "event" -> Some Event
        | _ -> None

type Member =
    {
        Name: string
        Kind: MemberKind
        /// Entity full name + "." + member name.
        FullName: string
        IsStatic: bool
        /// F# signature line, e.g. "val map: mapping: ('T -> 'U) -> list: 'T list -> 'U list".
        Signature: Lazy<string>
        /// Plain-text XML doc summary.
        Doc: Lazy<string option>
    }

type Entity =
    {
        Assembly: string
        /// Namespace path (may be empty).
        Namespace: string
        /// Display name with generic parameters, e.g. "Dictionary<'TKey,'TValue>".
        Name: string
        /// Namespace-qualified name without generic parameters, e.g. "System.Collections.Generic.Dictionary".
        FullName: string
        Kind: EntityKind
        Doc: Lazy<string option>
        Base: string option
        Interfaces: string list
        /// Target of an abbreviation, or the Invoke signature of a delegate, or the exception payload.
        Extra: string option
        IsObsolete: bool
        /// Struct union/record/class: rendered with [<Struct>].
        IsStruct: bool
        Members: Lazy<Member list>
    }

type Role =
    /// The thing the target names: the package, the dll, or (for a project) its non-framework references.
    | Primary
    /// Other package references or sibling dlls.
    | Dependency
    /// Reference assemblies of the target framework.
    | Framework

type Reference =
    {
        Path: string
        Name: string
        Role: Role
        PackageId: string option
        PackageVersion: string option
    }

type Scope =
    | PrimaryOnly
    | WithDependencies
    | AllReferences

type ResolvedTarget =
    {
        Target: string
        Description: string
        References: Reference list
        /// Problems worth telling the user about (e.g. project not built).
        Warnings: string list
        /// Files whose size or mtime changing means this resolution is stale.
        Watch: string list
    }

type Diagnostic =
    {
        Severity: string
        Line: int
        Column: int
        EndLine: int
        EndColumn: int
        Code: string
        Message: string
    }

type SearchHit =
    {
        Assembly: string
        FullName: string
        Kind: string
        Signature: string
        Doc: string option
        Score: int
    }
