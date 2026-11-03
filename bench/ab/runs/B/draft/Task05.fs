module Task05

open TorchSharp

let scalarSum (t: torch.Tensor) : float32 = t.sum().item<float32>()
