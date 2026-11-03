module Task18

open TorchSharp

let clampAndRound (t: torch.Tensor) : torch.Tensor =
    (t.clamp(0.0, 1.0) * 255.0).round().to_type(torch.uint8)
