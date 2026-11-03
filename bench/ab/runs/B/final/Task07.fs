module Task07

open TorchSharp

let softmaxLast (t: torch.Tensor) : torch.Tensor = torch.nn.functional.softmax(t, -1L)
