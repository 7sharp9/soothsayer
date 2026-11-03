module Task08

open TorchSharp

let argmaxRows (t: torch.Tensor) : torch.Tensor = t.argmax(1L)
