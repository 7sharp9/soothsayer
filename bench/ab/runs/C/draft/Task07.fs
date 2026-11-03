module Task07
open TorchSharp

let softmaxLast (t: torch.Tensor) : torch.Tensor = t.softmax(-1L)
