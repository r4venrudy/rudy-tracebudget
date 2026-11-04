"""Generate two deterministic synthetic OTLP JSON exports for the walkthrough."""
import argparse
import json
from pathlib import Path


def span(trace, identity, start, end, parent=None, name="GET /checkout"):
    row = {
        "traceId": f"{trace:032x}",
        "spanId": f"{identity:016x}",
        "name": name,
        "startTimeUnixNano": str(start),
        "endTimeUnixNano": str(end),
    }
    if parent is not None:
        row["parentSpanId"] = f"{parent:016x}"
    return row


def export(spans, service="checkout"):
    return {"resourceSpans": [{
        "resource": {"attributes": [{"key": "service.name", "value": {"stringValue": service}}]},
        "scopeSpans": [{"spans": spans}]
    }]}


def generate(directory):
    directory.mkdir(parents=True, exist_ok=True)
    origin = 1_780_000_000_000_000_000
    for file, growth in (("baseline.json", 0), ("candidate.json", 20_000_000)):
        rows = []
        for trace in range(1, 25):
            duration = 50_000_000 + trace * 500_000 + growth
            rows.extend([span(trace, 1, origin, origin + duration),
                         span(trace, 2, origin + 5_000_000, origin + 22_000_000, 1, "load-cart"),
                         span(trace, 3, origin + 10_000_000, origin + 30_000_000 + growth, 1, "quote")])
        (directory / file).write_text(json.dumps(export(rows), indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    generate(parser.parse_args().directory)
