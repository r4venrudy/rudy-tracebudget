"""Black-box CLI regressions and a discrete-time interval oracle."""
import importlib.util
import json
from pathlib import Path
import random
import subprocess
import sys
from tempfile import TemporaryDirectory

project = Path(__file__).resolve().parents[1]
command = [str(Path(arg).resolve()) for arg in sys.argv[1:]]
spec = importlib.util.spec_from_file_location("fixture", project / "examples/make_traces.py")
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
span, export = fixture.span, fixture.export
checks = 0


def check(condition, context):
    global checks
    checks += 1
    if not condition:
        raise AssertionError(context)


with TemporaryDirectory() as scratch:
    directory = Path(scratch)
    fixture.generate(directory)

    def invoke(*args, expected=0):
        result = subprocess.run([*command, *args], cwd=directory, capture_output=True,
                                text=True, encoding="utf-8", timeout=30)
        check(result.returncode == expected, (args, result.returncode, result.stderr, result.stdout[:300]))
        if expected == 2:
            check(not result.stdout and "tracebudget:" in result.stderr, result)
            return result.stderr
        check(not result.stderr, result.stderr)
        return json.loads(result.stdout)

    def write(name, data):
        path = directory / name
        path.write_bytes(data if isinstance(data, bytes) else json.dumps(data).encode())
        return name

    def inspect(data, *, labels=False, expected=0):
        path = write("-input.json", data)
        return invoke("inspect", *(["--include-labels"] if labels else []), "--", path, expected=expected)

    published = invoke("compare", "--max-delta-ns", "10000000", "--include-labels",
                       "baseline.json", "candidate.json", expected=1)
    row = published["routes"][0]
    check(published["status"] == "regressed" and row["p95DeltaNs"] == "20000000", published)
    check(row["before"] == {"count": 24, "p50Ns": "56000000", "p95Ns": "61500000"}, row)
    check(row["service"] == "checkout" and row["name"] == "GET /checkout", row)
    passed = invoke("compare", "--max-delta-ns", "20000000", "baseline.json", "candidate.json")
    check(passed["status"] == "within-budget" and passed["routes"][0]["name"] is None, passed)
    low_sample = invoke("compare", "--min-samples", "25", "baseline.json", "candidate.json", expected=3)
    check(low_sample["routes"][0]["status"] == "insufficient-samples", low_sample)
    reversed_result = invoke("compare", "candidate.json", "baseline.json")
    check(reversed_result["routes"][0]["p95DeltaNs"] == "-20000000", reversed_result)

    sample = invoke("inspect", "baseline.json")
    first = sample["traces"][0]
    check(first["qualified"] and first["traceId"] is None, first)
    check(first["spans"][0]["coveredNs"] == "25000000", first)
    check(first["spans"][0]["uncoveredNs"] == "25500000", first)
    check(first["spans"][0]["peakChildren"] == 2 and first["spans"][1]["parent"] == 1, first)
    check(first["spans"][0]["startNs"] == "0" and first["spans"][0]["service"] is None, first)

    malformed = export([span(1, 1, 0, 20), span(1, 2, 1, 2, 3),
                        span(1, 3, 1, 2, 2), span(1, 4, 0, 1, 7),
                        span(1, 5, 19, 22, 1), span(1, 6, 0, 1, 6)])
    bad_trace = inspect(malformed)["traces"][0]
    check((bad_trace["roots"], bad_trace["missingParents"], bad_trace["cycleMembers"],
           bad_trace["outsideParent"]) == (1, 1, 3, 1), bad_trace)
    check(not bad_trace["qualified"] and bad_trace["spans"][0]["coveredNs"] == "1", bad_trace)
    check(bad_trace["spans"][3]["parentMissing"], bad_trace)
    malformed_path = write("bad.json", malformed)
    incomplete = invoke("compare", "--min-samples", "1", "baseline.json", malformed_path, expected=3)
    check(incomplete["afterExcluded"] == 1 and incomplete["routes"][0]["after"]["count"] == 0, incomplete)

    max_ns = (1 << 64) - 1
    huge_path = write("huge.json", export([span(1, 1, 0, max_ns)]))
    zero_path = write("zero.json", export([span(1, 1, 0, 0)]))
    huge = invoke("compare", "--min-samples", "1", zero_path, huge_path, expected=1)
    check(huge["routes"][0]["p95DeltaNs"] == str(max_ns), huge)
    tiny = invoke("compare", "--min-samples", "1", huge_path, zero_path)
    check(tiny["routes"][0]["p95DeltaNs"] == "-" + str(max_ns), tiny)

    for invalid in ({}, [], b'{"resourceSpans":null}', b'{"resourceSpans":[],"resourceSpans":[]}',
                    export([span(1, 1, 0, 1), span(1, 1, 0, 1)]),
                    export([span(1, 1, 1, 0)]), export([span(1, 0, 0, 1)]),
                    export([span(1, 1, 0, max_ns+1)])):
        inspect(invalid, expected=2)
    bad_text = span(1, 1, 0, 1, name="\ud800")
    inspect(export([bad_text]), expected=2)
    bad_time = span(1, 1, 0, 1)
    bad_time["endTimeUnixNano"] = "1\0"
    inspect(export([bad_time]), expected=2)
    invoke("compare", "--min-samples", "0", "baseline.json", "candidate.json", expected=2)
    invoke("inspect", "--unknown", "baseline.json", expected=2)

    rng = random.Random(84111)
    all_rows = []
    layouts = []
    for trace in range(1, 181):
        bounds = [(0, 40)]
        parents = [None]
        for _ in range(rng.randrange(6, 35)):
            parent = rng.randrange(len(bounds))
            start, end = bounds[parent]
            new_start = rng.randrange(start, end+1)
            new_end = rng.randrange(new_start, end+1)
            bounds.append((new_start, new_end))
            parents.append(parent)
        origin = max_ns - 100 if trace % 2 else 0
        rows = [span(trace, index + 1, origin + start, origin + end,
                     None if parent is None else parent + 1)
                for index, ((start, end), parent) in enumerate(zip(bounds, parents))]
        rng.shuffle(rows)
        all_rows.extend(rows)
        layouts.append((bounds, parents))
    rng.shuffle(all_rows)
    result = inspect(export(all_rows), labels=True)
    check(len(result["traces"]) == len(layouts), "trace count")
    for report, (bounds, parents) in zip(result["traces"], layouts, strict=True):
        check(report["qualified"] and len(report["spans"]) == len(bounds), report)
        for index, (actual, (start, end)) in enumerate(zip(report["spans"], bounds, strict=True)):
            direct = [limits for limits, parent in zip(bounds, parents) if parent == index]
            ticks = [sum(a <= t < b for a, b in direct) for t in range(start, end)]
            union = sum(n != 0 for n in ticks)
            check(int(actual["coveredNs"]) == union, actual)
            check(int(actual["uncoveredNs"]) == end - start - union, actual)
            check(actual["peakChildren"] == max(ticks, default=0), actual)
            check(int(actual["startNs"]) == start and int(actual["durationNs"]) == end-start, actual)
            check(actual["parent"] == (None if parents[index] is None else parents[index]+1), actual)

    deep = [span(1, i, 0, 1, i-1 if i > 1 else None) for i in range(1, 7501)]
    deep_report = inspect(export(deep))["traces"][0]
    check(deep_report["qualified"] and len(deep_report["spans"]) == len(deep), "iterative chain")
print(f"{checks} checks across 180 generated traces, a 7,500-span chain, and fixed cases")
