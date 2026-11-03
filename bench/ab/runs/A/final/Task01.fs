module Task01

open TorchSharp

let zerosLike (t: torch.Tensor) : torch.Tensor = torch.zeros_like t
