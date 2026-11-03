module Task16

open TorchSharp

let noGradEval (model: torch.nn.Module<torch.Tensor, torch.Tensor>) (x: torch.Tensor) : torch.Tensor =
    model.eval() |> ignore
    use _ng = torch.no_grad()
    model.forward(x)
