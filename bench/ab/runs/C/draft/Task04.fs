module Task04
open TorchSharp

let squeezeFirst (t: torch.Tensor) : torch.Tensor =
    if t.shape.Length > 0 && t.shape.[0] = 1L then t.squeeze(System.Nullable 0L) else t
