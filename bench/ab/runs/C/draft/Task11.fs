module Task11
open TorchSharp

let mlp (inputs: int64) (hidden: int64) (outputs: int64) : torch.nn.Module<torch.Tensor, torch.Tensor> =
    torch.nn.Sequential(
        torch.nn.Linear(inputs, hidden),
        torch.nn.ReLU(),
        torch.nn.Linear(hidden, outputs))
