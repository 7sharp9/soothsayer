# A/B tasks: F# against TorchSharp 0.107.0

Each task is one file `TaskNN.fs` (NN = 01..20) containing `module TaskNN` and the named function or value with exactly
the stated F# signature. `open TorchSharp` and `open type TorchSharp.torch` are allowed; nothing outside TorchSharp
0.107.0, FSharp.Core and the .NET 10 BCL. The file must compile on its own. Behaviour should match the description, but
scoring is by compilation first.

01. `zerosLike : t: torch.Tensor -> torch.Tensor`: a tensor of zeros with the same shape, dtype and device as `t`.
02. `randMatrix : rows: int64 -> cols: int64 -> torch.Tensor`: a float32 tensor of shape [rows; cols] from a standard
    normal distribution.
03. `shapeOf : t: torch.Tensor -> int64 list`: the tensor's shape as an F# list.
04. `squeezeFirst : t: torch.Tensor -> torch.Tensor`: remove dimension 0 if it has size 1, otherwise return `t`
    unchanged.
05. `scalarSum : t: torch.Tensor -> float32`: sum all elements and return the result as an F# float32.
06. `batchMatmul : a: torch.Tensor -> b: torch.Tensor -> torch.Tensor`: batched matrix multiply of [B;N;M] by [B;M;P].
07. `softmaxLast : t: torch.Tensor -> torch.Tensor`: softmax over the last dimension.
08. `argmaxRows : t: torch.Tensor -> torch.Tensor`: index of the max element in each row of a 2-D tensor, as an int64
    tensor of shape [rows].
09. `concatRows : ts: torch.Tensor list -> torch.Tensor`: concatenate tensors along dimension 0.
10. `toDevice : useCuda: bool -> t: torch.Tensor -> torch.Tensor`: move `t` to CUDA when `useCuda` is true and CUDA is
    available, otherwise to CPU.
11. `mlp : inputs: int64 -> hidden: int64 -> outputs: int64 -> torch.nn.Module<torch.Tensor, torch.Tensor>`: Linear,
    ReLU, Linear, built with `torch.nn.Sequential`.
12. `type TwoLayer` with constructor `TwoLayer(inputs: int64, outputs: int64)`: a custom module inheriting
    `torch.nn.Module<torch.Tensor, torch.Tensor>`, holding one Linear layer registered as a submodule, whose `forward`
    applies the layer then tanh.
13. `mseLoss : pred: torch.Tensor -> target: torch.Tensor -> torch.Tensor`: mean squared error using TorchSharp's
    functional or module loss.
14. `trainStep : model: torch.nn.Module<torch.Tensor, torch.Tensor> -> opt: torch.optim.Optimizer -> x: torch.Tensor ->
    y: torch.Tensor -> float32`: one step of zero_grad, forward, MSE loss, backward, optimizer step; return the loss as
    float32.
15. `adam : model: torch.nn.Module<torch.Tensor, torch.Tensor> -> lr: float -> torch.optim.Optimizer`: an Adam optimizer
    over the model's parameters with learning rate `lr`.
16. `noGradEval : model: torch.nn.Module<torch.Tensor, torch.Tensor> -> x: torch.Tensor -> torch.Tensor`: switch the
    model to eval mode and run it on `x` with gradient tracking disabled.
17. `saveAndLoad : model: torch.nn.Module<torch.Tensor, torch.Tensor> -> path: string -> unit`: save the model's weights
    to `path`, then load them back into the same model.
18. `clampAndRound : t: torch.Tensor -> torch.Tensor`: clamp values to [0, 1], multiply by 255, round, and convert to
    uint8 (byte) dtype.
19. `oneHot : labels: torch.Tensor -> classes: int64 -> torch.Tensor`: one-hot encode an int64 label tensor of shape [N]
    into float32 [N; classes].
20. `withSeed : seed: int64 -> f: (unit -> 'T) -> 'T`: set TorchSharp's manual random seed to `seed`, then call `f`.
