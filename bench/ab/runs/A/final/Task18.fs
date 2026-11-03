module Task18

open TorchSharp

let clampAndRound (t: torch.Tensor) : torch.Tensor =
    t.clamp(0.0.ToScalar(), 1.0.ToScalar()).mul(255.0.ToScalar()).round().to_type(torch.uint8)
