#!/usr/bin/env python3
"""Phase 2 item 0, round 1: does each candidate model fit fully on the card at the prompt
size the pipeline needs, and how fast is it?

Run with the compose stack up:  python3 ops/bench/round1.py
Standard library only. Every run starts with nothing loaded, so each one includes a cold start.
"""
import json
import statistics
import subprocess
import time
import urllib.request
from datetime import datetime
from pathlib import Path

OLLAMA = "http://localhost:11434"
# The 0121 run also had qwen3:14b. It was dropped after it didn't fit on the card (D-6);
# this list produces the 0147 run.
MODELS = ["gemma4:12b-it-qat", "qwen3.5:9b"]
CONTEXTS = [16384, 32768]  # the realistic target, and headroom
RUNS = 3  # a laptop card slows down when hot; the median of 3 smooths that out
FILLER_CHARS = 40_000  # ~10k word-pieces, the pipeline's estimated prompt size

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]


def call(path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(OLLAMA + path, data, {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as resp:
        return json.load(resp)


def loaded():
    return {m["name"]: m for m in call("/api/ps")["models"]}


def unload_all():
    # Unloading is asynchronous, so wait until the card is empty; otherwise the previous
    # model's memory is counted against the next one.
    for name in loaded():
        call("/api/generate", {"model": name, "keep_alive": 0})
    for _ in range(60):
        if not loaded():
            return
        time.sleep(0.5)
    raise RuntimeError(f"models still loaded after 30s: {list(loaded())}")


def gpu_used_mib():
    out = subprocess.run(
        ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
        capture_output=True, text=True, check=True,
    ).stdout
    return int(out.split()[0])


def measure(model, ctx, prompt):
    unload_all()
    r = call("/api/generate", {
        "model": model,
        "prompt": prompt,
        "stream": False,
        "think": False,  # thinking changes answer length, not per-token speed; that's round 2
        "keep_alive": "10m",
        "options": {"num_ctx": ctx, "temperature": 0, "seed": 42, "num_predict": 400},
    })
    ps = loaded()[model]  # read while the model is still loaded
    return {
        "model": model,
        "ctx": ctx,
        "cold_start_s": r["load_duration"] / 1e9,
        "prompt_tokens": r["prompt_eval_count"],
        "prompt_tok_s": r["prompt_eval_count"] / (r["prompt_eval_duration"] / 1e9),
        "out_tok_s": r["eval_count"] / (r["eval_duration"] / 1e9),
        "gpu_pct": 100 * ps["size_vram"] / ps["size"],  # anything under 100 runs partly on the processor
        "ollama_size_gb": ps["size"] / 1e9,
        "card_used_mib": gpu_used_mib(),
    }


def main():
    # The prompt is the concatenation of all the docs, truncated to FILLER_CHARS.
    # This is a realistic prompt size for the pipeline.
    prompt = "Summarise this in 5 bullet points:\n" + "".join(
        p.read_text() for p in sorted((REPO / "docs").glob("*.md"))
    )[:FILLER_CHARS]

    version = call("/api/version")["version"]
    out_path = HERE / f"round1-{datetime.now():%Y%m%d-%H%M}.jsonl"
    print(f"Ollama {version}; raw results -> {out_path}")

    rows = []
    with out_path.open("w") as out:
        for model in MODELS:
            for ctx in CONTEXTS:
                for run in range(1, RUNS + 1):
                    print(f"  {model} ctx={ctx} run {run}/{RUNS} ...", flush=True)
                    row = measure(model, ctx, prompt) | {"run": run, "ollama_version": version}
                    out.write(json.dumps(row) + "\n")
                    rows.append(row)
    unload_all()

    cols = ["gpu_pct", "ollama_size_gb", "card_used_mib", "cold_start_s", "prompt_tokens", "prompt_tok_s", "out_tok_s"]
    print("\nMedian of", RUNS, "runs")
    print(f"{'model':<20}{'ctx':>7}" + "".join(f"{c:>16}" for c in cols))
    for model in MODELS:
        for ctx in CONTEXTS:
            group = [r for r in rows if r["model"] == model and r["ctx"] == ctx]
            cells = "".join(f"{statistics.median(r[c] for r in group):>16.1f}" for c in cols)
            print(f"{model:<20}{ctx:>7}{cells}")


if __name__ == "__main__":
    main()
