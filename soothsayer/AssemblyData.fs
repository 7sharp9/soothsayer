module AssemblyData
    type MemberClassification = 
        | Ctor = 0 | Member = 1 | Val = 2

    type TypeClassification =
        Type = 0 | Class = 1 | Union = 2 | Record = 3 | Struct = 4 | Enum = 5 | Interface = 6 | Measure = 7

    type SMember = SMember of name : string *  ``type`` : MemberClassification * signature : string

    type SType = SType of name : string * ``type`` : TypeClassification * members : SMember list

    type SModule = SModule of name : string * entities : Ents list

    and SNamespace = SNamespace of name:string * entities : Ents list

    and Ents =
        | Member of SMember
        | Type of SType
        | Module of SModule
        | Namespace of SNamespace

    type Assembly = {name : string; entities: Ents list}