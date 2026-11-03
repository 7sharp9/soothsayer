# Soothsayer as a batch typecheck oracle

`soothsayer score <target> <in.jsonl> [--workers n]` typechecks one snippet per input line and writes one JSON result
per line. On TorchSharp 0.107.0 it sustains about 124 snippets a second in one process and about 240 with four
processes, against about 0.39 a second for `dotnet build` on the same machine. Its pass/fail verdict and its set of
error codes matched `dotnet build` on all 40 sampled snippets.

## Corpus

[make_corpus.py](make_corpus.py) takes the 100 files written in the A/B run (`../ab/runs`) and adds mutations of each:

- name: up to four `.member(` call sites, each renamed with a `zz` suffix (expected FS0039)
- type: up to three `int64` literals turned into strings (expected a type error)
- syntax: the last `)` removed (expected a parse error)

That gives 379 snippets, 214 with distinct text (`distinct.jsonl`). For throughput the 214 were repeated 20 times with a
unique trailing comment, 4,280 snippets, so the in-process cache of identical text never hit (`cached` was 0 of 4,280).

## Throughput

Machine: 16 cores (8 performance). Load average was about 13 during the runs from other work, so these figures are
conservative. Each run starts cold: it resolves the target and loads references in every worker process. Three
interleaved rounds, 4,280 snippets ([interleaved.txt](interleaved.txt)):

| Workers | Wall time (s), rounds 1 to 3 | Snippets/s, mean |
|---|---|---|
| 1 | 35.18, 34.44, 33.57 | 124.5 |
| 2 | 23.67, 24.05, 24.57 | 177.7 |
| 4 | 17.13, 18.50, 18.03 | 239.5 |
| 8 | 16.71, 16.75, 18.45 | 247.9 |

The first, non-interleaved runs were much slower (58.8 s for one worker) and are not used; the interleaved rounds are
consistent with each other.

From the one-worker runs at two corpus sizes (214 snippets in about 7.1 s, 4,280 in about 34.5 s), startup is about 5.7
s and each check about 6.7 ms. A separate single-worker run measured a median of 5.7 ms per snippet. Scaling flattens
beyond four workers. With 4,280 snippets over eight workers, each worker spends about as long starting up as checking,
so the plateau is at least partly startup cost; a larger batch would be needed to separate that from contention.

For comparison, `dotnet build` of the same snippets one at a time took 51.4 s for 20 files, 2.57 s per file (0.39/s).
The warm MCP server took 0.068 s per `check_snippet` call. The batch path is about ten times faster per check than the
MCP path; the per-call target fingerprint (a stat of about 170 dlls) and JSON-RPC framing are likely causes, not
measured separately.

## Correctness

- Mutations: every one of the 158 mutated snippets failed. 152 were reported in the expected class. Six name mutations
  were reported as API misuse instead: three renamed an `override` (FS0855, nothing to override), and three were in a
  draft that already had an FS0041 overload error.
- Originals: 53 of 56 distinct originals pass; the three that fail are the three drafts that failed `dotnet build` in
  the A/B run.
- Against `dotnet build`, on 40 snippets sampled at random from the 214 (13 build, 27 fail): pass/fail agreed on 40 of
  40, and the set of error codes was identical on 40 of 40.

The error-code agreement needed one change. `fsc` stops after a parse error; the first version of `score` also
typechecked the recovered tree and added FS0039 or FS0041 errors that were artefacts of the syntax error (4 of the 40
differed). `score` now reports only parse errors when parsing fails (`Snippet.checkParseFirst`). That fixed the
agreement and made no measurable difference to throughput.

## What 10,000 samples would cost

At the measured rates: `dotnet build`, about 7.1 hours (10,000 × 2.57 s); `score` with one worker, about 73 s (5.7 s +
10,000 × 6.7 ms); with four workers, about 42 s.

## Limits

- One target (TorchSharp 0.107.0), one machine, a busy one.
- The corpus is mutations of 20 tasks, not real model output across many tasks. Real samples will be more varied, and
  some will be pathological for type inference; there is no per-snippet timeout yet.
- Error classes are a fixed table of FS codes in `src/Soothsayer.Cli/Score.fs`; anything unlisted is `other`.
- The `score` command and `checkParseFirst` have no unit tests yet; the evidence is the agreement runs above.
