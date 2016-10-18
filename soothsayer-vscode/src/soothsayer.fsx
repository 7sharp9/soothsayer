#r "../node_modules/fable-core/Fable.Core.dll"
#r "../node_modules/fable-powerpack/Fable.PowerPack.dll"
module Soothsayer =
    open Fable.Core
    open Fable.PowerPack
    let test =
        promise {
            return ""
        }
    printfn "Hello"