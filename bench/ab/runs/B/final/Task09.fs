module Task09

open TorchSharp

let concatRows (ts: torch.Tensor list) : torch.Tensor = torch.cat(List.toArray ts, 0L)
