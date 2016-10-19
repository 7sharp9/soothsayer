#r "../node_modules/fable-core/Fable.Core.dll"
#r "../node_modules/fable-powerpack/Fable.PowerPack.dll"
#load "../../paket-files/Ionide/ionide-vscode-helpers/Fable.Import.VSCode.fs"
#load "../../paket-files/Ionide/ionide-vscode-helpers/Helpers.fs"

open System
open Fable.Core
open Fable.Import
open Fable.Import.JS
open Fable.Import.Node

[<Erase>]
module child_process_promise =

    [<AutoOpen>]    
    type Globals =
        member x.exec(cmd:string, ?options:obj) : Promise<child_process_types.ChildProcess> = Exceptions.jsNative

    [<Import("*", "child-process-promise")>]
    let child_process_promise : Globals = Exceptions.jsNative

module Soothsayer =
    open Fable.PowerPack
    open Fable.Import.Browser

    open Ionide.VSCode.Helpers

    open child_process_promise
    open Fable.Import.Node
    open Fable.Core.JsInterop
    open Fable.Import.vscode
    
    let resolverExe = VSCode.getPluginPath "7sharp9.soothsayer" + "/bin/Resolver.exe"
    let choosenAssembly = ""
    let choosenProject = "~/fsharp/soothsayer-addin/soothsayer/soothsayer.fsproj" //TODO: collection from quickpick

    let createCommand command args =
        if Process.isWin() then command + " " + args
        else "mono" + " " + command + " " + args


    let runResolver =
        promise {
            let options =
                createObj [
                    "cwd" ==> workspace.rootPath
                    //TODO: set max size of buffer later, only really applicable for soothsayer stdio as it will be large
                ]

            let! result = child_process_promise.exec(createCommand resolverExe choosenProject, options)
            let result, error = result.stdout, result.stderr
            console.log(result.ToString())
            //ignore error assume the best!
            return result.ToString()
        }
