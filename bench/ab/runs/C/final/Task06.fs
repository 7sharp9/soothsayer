module Task06
open TorchSharp

let batchMatmul (a: torch.Tensor) (b: torch.Tensor) : torch.Tensor = torch.bmm(a, b)
