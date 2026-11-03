module Task02
open TorchSharp

let randMatrix (rows: int64) (cols: int64) : torch.Tensor = torch.randn(rows, cols, dtype = System.Nullable torch.float32)
