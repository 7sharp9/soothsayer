module Task10

open TorchSharp

let toDevice (useCuda: bool) (t: torch.Tensor) : torch.Tensor =
    if useCuda && torch.cuda.is_available () then t.cuda () else t.cpu ()
