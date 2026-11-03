module Task12

open TorchSharp

type TwoLayer(inputs: int64, outputs: int64) as this =
    inherit torch.nn.Module<torch.Tensor, torch.Tensor>("TwoLayer")
    let lin = torch.nn.Linear(inputs, outputs)
    do this.RegisterComponents()
    override _.forward(x: torch.Tensor) : torch.Tensor = torch.tanh(lin.forward(x))
