module Task09

open TorchSharp

let concatRows (ts: torch.Tensor list) : torch.Tensor = torch.cat(ts, 0L)
