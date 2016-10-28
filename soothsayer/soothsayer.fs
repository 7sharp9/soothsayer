module Soothsayer =
    open System
    open System.IO
    open Microsoft.FSharp.Compiler
    open Microsoft.FSharp.Compiler.SourceCodeServices
    open System.Reflection
    open AssemblyData

    let checker = SourceCodeServices.FSharpChecker.Create(keepAssemblyContents=true,ImplicitlyStartBackgroundWork=false)

    let allRefs assembly =
        let base1 = Path.GetTempFileName()
        let fileName1 = Path.ChangeExtension(base1, ".fs")
        let projectFilename = Path.ChangeExtension(base1, ".fsproj")
        let moduleText = """
    module Test
    """
        File.WriteAllText(fileName1, moduleText)

        let ass = System.Reflection.Assembly.ReflectionOnlyLoadFrom assembly
        let refass = ass.GetReferencedAssemblies()
        let deps =
            [for a in refass do
             let aa = System.Reflection.Assembly.ReflectionOnlyLoad(a.FullName)
             yield sprintf "-r:%s" aa.Location ]
        
        let projoptions =
            let options = [|
                yield "--simpleresolution"
                yield "--out:" + System.IO.Path.ChangeExtension(fileName1, ".exe")
                yield "--platform:anycpu"
                yield "--fullpaths"
                yield "--flaterrors"
                yield "--target:exe"
                yield "--noframework"
                yield sprintf "-r:%s" assembly
                yield! deps
                yield fileName1 |]
            checker.GetProjectOptionsFromCommandLineArgs(projectFilename, options)

        //--------------------------------------------------------

        // let parseResults2, checkFileAnswer2 = 
        //     checker.ParseAndCheckFileInProject(fileName1, 0, moduleText, projoptions) 
        //     |> Async.RunSynchronously

        // let checkFileResults = 
        //     match checkFileAnswer2 with
        //     | FSharpCheckFileAnswer.Succeeded(res) ->
        //         //printfn "succeeded:\n%A" res
        //         //printfn "%A" res.PartialAssemblySignature.Entities.[0].MembersFunctionsAndValues
        //         ()
        //     | res -> failwithf "Parsing did not finish... (%A)" res

        let results = checker.ParseAndCheckProject projoptions |> Async.RunSynchronously
        // let allSymbols = results.GetAllUsesOfAllSymbols() |> Async.RunSynchronously
        // let first = allSymbols.[0]
        // let dc = first.DisplayContext
        let refAssemblies = results.ProjectContext.GetReferencedAssemblies()
        let aaa = refAssemblies |> List.find (fun a -> a.SimpleName = Path.GetFileNameWithoutExtension(assembly))

        let hasNested (ent:FSharpEntity) =
            ent.NestedEntities.Count > 0

        let removals =
            ["Microsoft.FSharp.Core."
             "Microsoft.FSharp.Collections."
            ]

        let replacements =
            ["&", "&amp;"
             "<", "&lt;"
             ">", "&gt;"
             "\"", "&quot;"
             "'", "&#39;"
             "/", "&#x2F;"
             "`", "&#x60;"
             "=", "&#x3D;"]

        let cleanSig (name:string) =
            let rec remove (temp:string) (removals: string list) =
                match removals with
                | [] -> temp
                | r :: tail ->
                    remove (temp.Replace(r, "")) tail
            remove name removals

        let cleanTypeName name =
            let rec remove (temp:string) (removals: (string * string) list) =
                match removals with
                | [] -> temp
                | (r, rw) :: tail ->
                    remove (temp.Replace(r, rw)) tail
            remove name replacements


        let mapMember (m:FSharpMemberOrFunctionOrValue) =
            let getMemberFields (m:FSharpMemberOrFunctionOrValue) =
                if m.DisplayName = "( .ctor )" then SMember("new", MemberClassification.Ctor, cleanSig <| m.FullType.Format(FSharpDisplayContext.Empty))
                elif m.IsMember then SMember(m.DisplayName, MemberClassification.Member, cleanSig <| m.FullType.Format(FSharpDisplayContext.Empty))
                else SMember(m.DisplayName, MemberClassification.Val, cleanSig <| m.FullType.Format(FSharpDisplayContext.Empty))
            m |> getMemberFields

        let rec mapNamespace (ent:FSharpEntity) =
            Namespace(SNamespace(ent.DisplayName, ent.NestedEntities |> Seq.map mapEntity |> Seq.toList))
        
        and mapTypeBy (ent:FSharpEntity) (classification:TypeClassification) =
            Type(SType(cleanTypeName ent.DisplayName, classification, ent.MembersFunctionsAndValues |> Seq.map mapMember |> Seq.toList))

        and mapModule (ent:FSharpEntity) =
            Module (SModule(ent.DisplayName, ent.NestedEntities |> Seq.map mapEntity |> Seq.toList ))

        and mapEntity (ent:FSharpEntity) : Ents =
            if ent.IsFSharpModule then mapModule ent
            elif ent.IsNamespace then mapNamespace ent
            elif ent.IsValueType then mapTypeBy ent TypeClassification.Struct
            elif ent.IsFSharpRecord then mapTypeBy ent TypeClassification.Record
            elif ent.IsFSharpUnion then mapTypeBy ent TypeClassification.Union
            elif ent.IsClass && not ent.IsFSharp then mapTypeBy ent TypeClassification.Class
            elif ent.IsFSharp && ent.IsClass then mapTypeBy ent TypeClassification.Type
            elif ent.IsEnum then mapTypeBy ent TypeClassification.Enum
            elif ent.IsMeasure then mapTypeBy ent TypeClassification.Measure
            elif ent.IsInterface then mapTypeBy ent TypeClassification.Interface
            else mapTypeBy ent TypeClassification.Type
        
        let entities = aaa.Contents.Entities |> Seq.map mapEntity |> Seq.toList
        let assembly = {name = aaa.SimpleName; entities = entities}
        assembly

module Fable =
    open Newtonsoft.Json
    let serialize v =
        let settings =
            JsonSerializerSettings(Converters = [|Fable.JsonConverter()|])
        JsonConvert.SerializeObject(v, settings)

[<EntryPoint>]
let main argv =
    let assembly = Soothsayer.allRefs argv.[0]
    let result = Fable.serialize assembly
    System.Console.WriteLine result
    0 // return an integer exit code
