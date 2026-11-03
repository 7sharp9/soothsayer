module Task13
open TorchSharp

let mseLoss (pred: torch.Tensor) (target: torch.Tensor) : torch.Tensor =
    torch.nn.functional.mse_loss(pred, target)
