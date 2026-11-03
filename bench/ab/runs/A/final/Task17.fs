module Task17

open TorchSharp

let saveAndLoad (model: torch.nn.Module<torch.Tensor, torch.Tensor>) (path: string) : unit =
    model.save path |> ignore
    model.load path |> ignore
