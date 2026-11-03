module Task18

open TorchSharp

let clampAndRound (t: torch.Tensor) : torch.Tensor =
    let c = t.clamp((0.0).ToScalar(), (1.0).ToScalar())
    let m = c.mul((255.0).ToScalar())
    m.round().to_type(torch.uint8)
