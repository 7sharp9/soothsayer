module Task11

open TorchSharp

let mlp (inputs: int64) (hidden: int64) (outputs: int64) : torch.nn.Module<torch.Tensor, torch.Tensor> =
    let l1 = torch.nn.Linear(inputs, hidden) :> torch.nn.Module<torch.Tensor, torch.Tensor>
    let act = torch.nn.ReLU() :> torch.nn.Module<torch.Tensor, torch.Tensor>
    let l2 = torch.nn.Linear(hidden, outputs) :> torch.nn.Module<torch.Tensor, torch.Tensor>
    torch.nn.Sequential([| l1; act; l2 |]) :> torch.nn.Module<torch.Tensor, torch.Tensor>
