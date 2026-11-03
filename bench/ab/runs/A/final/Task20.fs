module Task20

open TorchSharp

let withSeed (seed: int64) (f: unit -> 'T) : 'T =
    torch.manual_seed seed |> ignore
    f ()
