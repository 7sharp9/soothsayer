module Task04

open TorchSharp

let squeezeFirst (t: torch.Tensor) : torch.Tensor =
    if t.dim () > 0 && t.shape.[0] = 1L then t.squeeze(0L) else t
