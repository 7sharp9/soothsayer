module Task03

open TorchSharp

let shapeOf (t: torch.Tensor) : int64 list = t.shape |> List.ofArray
