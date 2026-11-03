module Task19

open TorchSharp

let oneHot (labels: torch.Tensor) (classes: int64) : torch.Tensor =
    torch.nn.functional.one_hot(labels, classes).to_type(torch.float32)
