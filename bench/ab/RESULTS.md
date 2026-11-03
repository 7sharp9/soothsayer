# A/B: does Soothsayer help an agent write F# against TorchSharp?

On these 20 tasks it did not. The model wrote all 20 correctly from memory, so there was no accuracy left for Soothsayer
to recover, and the Soothsayer arm was the slowest by a wide margin. This is a ceiling effect on mainstream TorchSharp
API, not evidence about obscure or post-cutoff APIs.

## Setup

- Tasks: [tasks.md](tasks.md), 20 single-function tasks against TorchSharp 0.107.0, written before any arm ran.
- Model: the same model (Claude Opus 5.5, general-purpose subagent) for every arm, one run per arm.
- Arms, each in its own folder under `runs/`:
  - A, memory only: read the tasks, write files, no shell.
  - B, compiler loop: write all drafts first, then `dotnet build` a scratch project, at most 3 rounds per task, no doc
    reading.
  - C, Soothsayer: CLI lookups (`search`, `sig`, `outline`) at will, write all drafts, then `check`, at most 3 rounds
    per task, no `dotnet build`.
- Scoring: [score.sh](score.sh) builds each file on its own with plain `dotnet build` against TorchSharp 0.107.0,
  independent of Soothsayer. Positive control (a correct `zeros_like`) scores ok; negative control (`zeroos_like`)
  scores fail with FS0039. Per-task results are in each folder's `score.tsv`.

## Results

| Arm | Drafts compiling | Finals compiling | Wall time | Tokens | Checks or builds |
|---|---|---|---|---|---|
| A, memory | n/a | 20/20 | 43 s | 68,646 | 0 |
| B, compiler loop | 18/20 | 20/20 | 124 s | 67,206 | 22 builds |
| C, Soothsayer | 19/20 | 20/20 | 322 s | 76,194 | 35 lookups, 21 checks |

Wall time and tokens are from the harness's per-agent usage report. Build, lookup and check counts are the arms' own
reports; C logged its calls through a wrapper script.

Draft failures:

- B Task09: `torch.cat` given an F# list (FS0041; it takes an array, `IList` or span). Fixed in one round.
- B Task18 and C Task18: `t.clamp(0.0, 1.0)` is ambiguous between the `Scalar` and `Tensor` overloads (FS0041). Fixed
  with `.ToScalar()`. C had looked up `clamp` before drafting and still hit this: the signature shows `?min: Scalar`,
  but it does not tell the reader that a float literal will not resolve between the two overloads.

## What this shows

- Accuracy: no difference in final outcome. Every arm ended at 20/20, and memory alone was already there.
- Lookup before drafting saved one draft error relative to the compiler loop (19 vs 18), within the noise of a single
  run.
- Cost: C took 2.6 times the wall time of B and 7.5 times that of A. Part of that is CLI cold start (5 to 9 s per call
  to load references); the MCP server keeps them loaded and would be faster. That is not measured here.
- The compiler already gives name suggestions on FS0039 (`Maybe you want one of the following: zeros_like`), which is
  most of what a lookup tool offers when a name is wrong.

## Soothsayer as the oracle instead of the assistant

The same 20 arm-C final files, checked two ways on the same machine:

- `score.sh` (one `dotnet build` per file): 51.4 s wall for 20 files, 2.57 s per file, 20 pass.
- One warm `soothsayer mcp` process, `check_snippet` on each file three times over (60 calls): 3.97 s for the first call
  (loading references), then a mean of 0.117 s per call for the rest of the first pass and 0.068 s for passes two and
  three. All 60 report no errors.

That is about 38 times faster per file once warm (2.57 / 0.068). On the three drafts that failed to build (B Task09, B
Task18, C Task18), `check` reports the same error counts as the build: 1, 2 and 1. This is one machine and a single run
of each, and 23 files is a small agreement sample.

## Defect found

`Tensor.to` cannot be found: `sig TorchSharp.torch.Tensor.to` reports nothing, and `search "Tensor.to"` returns `topk`,
`tolist` and `to_type` instead. The method exists (`t.``to``(torch.CPU)` passes `check`). `to` is an F# keyword, so the
name probably needs backticks, and the search or extraction mishandles it. Any member named with an F# keyword is likely
affected.

## Limits

- One run per arm; no variance estimate.
- Compilation only. Behaviour was not run, because that needs the native libtorch package.
- The tasks cover mainstream API that the model has seen often. The case Soothsayer is meant for (less common packages,
  versions after the model's training data, API that changed between versions) was not tested.
- Rule compliance is self-reported; drafts and finals were checked on disk, commands were not audited.
