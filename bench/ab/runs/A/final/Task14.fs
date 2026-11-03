module Task14

open TorchSharp

let trainStep
    (model: torch.nn.Module<torch.Tensor, torch.Tensor>)
    (opt: torch.optim.Optimizer)
    (x: torch.Tensor)
    (y: torch.Tensor)
    : float32 =
    opt.zero_grad ()
    let pred = model.forward x
    let loss = torch.nn.functional.mse_loss (pred, y)
    loss.backward ()
    opt.step () |> ignore
    loss.item<float32> ()
