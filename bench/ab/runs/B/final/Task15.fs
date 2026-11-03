module Task15

open TorchSharp

let adam (model: torch.nn.Module<torch.Tensor, torch.Tensor>) (lr: float) : torch.optim.Optimizer =
    torch.optim.Adam(model.parameters(), lr)
