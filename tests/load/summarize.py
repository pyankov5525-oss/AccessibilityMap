#!/usr/bin/env python3
import json
import os
import sys

stage, path = sys.argv[1:3]
with open(path, encoding="utf-8") as handle:
    report = json.load(handle)
metrics = report.get("metrics", {})

def values(name):
    metric = metrics.get(name, {})
    return metric.get("values", metric)

def number(value, digits=2):
    return "n/a" if value is None else f"{value:.{digits}f}"

requests = values("http_reqs").get("count", values("http_reqs").get("value"))
iterations = values("iterations").get("count", values("iterations").get("value"))
failed = values("http_req_failed").get("rate")
duration = values("http_req_duration")
line = (
    f"stage={stage}; requests={requests}; iterations={iterations}; "
    f"failed={number(None if failed is None else failed * 100)}%; "
    f"avg={number(duration.get('avg'))}ms; p95={number(duration.get('p(95)'))}ms; "
    f"max={number(duration.get('max'))}ms"
)
print(line)
print(f"::notice title=Load test {stage} users::{line}")
summary = os.environ.get("GITHUB_STEP_SUMMARY")
if summary:
    with open(summary, "a", encoding="utf-8") as handle:
        handle.write(f"- `{line}`\n")
