"""Builds corpus.jsonl from the A/B run files plus three mutations of each.
Each line: {"id": "<arm>/<task>/<variant>", "code": ..., "expect": "ok"|"unknown_name"|"api_misuse"|"syntax"}."""
import glob, json, os, random, re, sys
root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "ab", "runs")
random.seed(7)
out = []
for f in sorted(glob.glob(os.path.join(root, "*", "*", "Task*.fs"))):
    arm = os.path.relpath(f, root).replace(os.sep, "/").rsplit("/", 1)[0]
    task = os.path.basename(f)[:-3]
    code = open(f).read()
    out.append({"id": f"{arm}/{task}/orig", "code": code, "expect": None})
    # invented member: rename one `.member(` call
    calls = list(re.finditer(r"\.([a-z_][A-Za-z_0-9]*)\s*\(", code))
    for k, m in enumerate(calls[:4]):
        mutated = code[:m.start(1)] + m.group(1) + "zz" + code[m.end(1):]
        out.append({"id": f"{arm}/{task}/name{k}", "code": mutated, "expect": "unknown_name"})
    # wrong literal type: an int64 literal becomes a string
    lits = list(re.finditer(r"(?<![\w.])(-?\d+)L\b", code))
    for k, m in enumerate(lits[:3]):
        mutated = code[:m.start()] + '"' + m.group(1) + '"' + code[m.end():]
        out.append({"id": f"{arm}/{task}/type{k}", "code": mutated, "expect": "api_misuse"})
    # syntax: drop the last closing paren
    i = code.rfind(")")
    if i >= 0:
        out.append({"id": f"{arm}/{task}/syntax", "code": code[:i] + code[i+1:], "expect": "syntax"})
with open(sys.argv[1], "w") as w:
    for o in out: w.write(json.dumps(o) + "\n")
print(len(out), "snippets;", len({o["code"] for o in out}), "distinct")
