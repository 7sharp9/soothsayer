#r "../node_modules/fable-core/Fable.Core.dll"
#r "../node_modules/fable-powerpack/Fable.PowerPack.dll"
#load "../../paket-files/Ionide/ionide-vscode-helpers/Fable.Import.VSCode.fs"
#load "../../paket-files/Ionide/ionide-vscode-helpers/Helpers.fs"
#load "../../soothsayer/AssemblyData.fs"

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Import
open Fable.Import.Browser
open Fable.Import.JS
open Fable.Import.Node

[<Erase>]
module Child_process_promise =

    [<AutoOpen>]    
    type Globals =
        member x.exec(cmd:string, ?options:obj) : Promise<child_process_types.ChildProcess> =
            Exceptions.jsNative

    [<Import("*", "child-process-promise")>]
    let child_process_promise : Globals = Exceptions.jsNative

module HtmlMapping =
    open AssemblyData
    let mapFunction f =
        ""
    
    let mapType t = 
        ""

    let mapNamespace n =
        ""

    let mapAssembly (a:AssemblyData.Assembly) =
        let mutable pretty = a.name + "\n"
        for entity in a.entities do
            match entity with
            | Namespace (SNamespace(name, entities)) -> pretty <- pretty + name + "\n"
            | Module (SModule(name, entities)) -> pretty <- pretty + name + "\n"
            | Type(SType(name, classification, members)) ->pretty <- pretty + name + "\n"
            | Member(SMember(name, classification, signature)) -> pretty <- pretty + name + "\n"
            //TODO recurse heavily on children
        pretty
            

    

module Soothsayer =
    open Fable.PowerPack
    open Ionide.VSCode.Helpers
    open Child_process_promise
    open Fable.Import.vscode
    
    let resolverExe = VSCode.getPluginPath "7sharp9.soothsayer" + "/bin/Resolver.exe"
    let soothsayerExe = VSCode.getPluginPath "7sharp9.soothsayer" + "/bin/soothsayer.exe"
    let choosenAssembly = ""
    let choosenProject = "~/fsharp/soothsayer-addin/soothsayer/soothsayer.fsproj"
    //TODO: collection from quickpick

    let createCommand command args =
        if Process.isWin() then command + " " + args
        else "mono" + " " + command + " " + args

    let createProcessOptionsWithBuffer size =
        createObj [
            "cwd" ==> workspace.rootPath
            "maxBuffer" ==> size
            //TODO: set max size of buffer later, only really applicable for soothsayer stdio as it will be large
        ]

    

    let runResolver() =
        promise {
            let options = createProcessOptionsWithBuffer (1024 * 500)

            let! result = child_process_promise.exec(createCommand resolverExe choosenProject, options)
            let result, error = result.stdout, result.stderr

            let! quickPickresult = 
                let inputList = 
                    result.ToString().Split([|"\r";"\n"|], StringSplitOptions.RemoveEmptyEntries)
                    |> ResizeArray
                vscode.window.showQuickPick (unbox inputList)
            let! informationMessageResult =  vscode.window.showInformationMessage <| sprintf "You picked: %s" quickPickresult
            //Now, after selection we need to send the chosen item to soothsayer
            let optionsForSoothsayer = createProcessOptionsWithBuffer (1024 * 4000)

            let! soothsayerResult = child_process_promise.exec(createCommand soothsayerExe quickPickresult, optionsForSoothsayer)
            let soothsayerResult, SoothsayerError = soothsayerResult.stdout.ToString(), soothsayerResult.stderr

            let assemblyDetail = Fable.Core.Serialize.ofJson<AssemblyData.Assembly> soothsayerResult
            
            let prettyHtml = HtmlMapping.mapAssembly assemblyDetail
            console.log prettyHtml
            //ignore error assume the best!
            return promise.Zero
        } |> ignore

let activate(context: vscode.ExtensionContext) =
    let registerCommand com (f: unit->unit) =
        vscode.commands.registerCommand(com, unbox f)
        |> context.subscriptions.Add

    registerCommand "extension.soothsayer" Soothsayer.runResolver
