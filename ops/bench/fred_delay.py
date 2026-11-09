#!/usr/bin/env python3
"""Phase 2 item 1: how long after BLS's 8:30 release does FRED post the figure? (D-22)

Run by hand on release morning, before 08:25 New York time:
    python3 ops/bench/fred_delay.py cpi 2026-10      # October CPI
    python3 ops/bench/fred_delay.py jobs 2026-11     # November jobs report
--now skips the wait for a test run; Ctrl-C stops early and still prints the summary.

Polls every 60 seconds until 11:00 and writes one JSON line per series per poll.
Standard library only. Reads Macro__Fred__ApiKey from the repo's .env.
"""
import argparse
import json
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

FRED = "https://api.stlouisfed.org/fred/"
NY = ZoneInfo("America/New_York")
SERIES = {"cpi": ["CPIAUCSL", "CPILFESL"], "jobs": ["PAYEMS", "UNRATE"]}
START, RELEASE, END = (8, 25), (8, 30), (11, 0)
INTERVAL = 60  # 4 requests a minute against FRED's 120

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]


def api_key():
    for line in (REPO / ".env").read_text().splitlines():
        name, sep, value = line.partition("=")
        if sep and name.strip() == "Macro__Fred__ApiKey":
            return value.strip().strip("\"'")
    raise SystemExit("Macro__Fred__ApiKey is not in .env")


def get(path, key, **params):
    # The key is in the query string, so the URL is never logged or printed.
    query = urllib.parse.urlencode({**params, "api_key": key, "file_type": "json"})
    with urllib.request.urlopen(f"{FRED}{path}?{query}", timeout=20) as resp:
        return json.load(resp)


def describe(error):
    if isinstance(error, urllib.error.HTTPError):
        try:
            message = json.load(error).get("error_message", "")
        except Exception:
            message = ""
        return f"HTTP {error.code} {message}".strip()
    return f"{type(error).__name__}: {error}"  # URLError and timeouts carry no URL


def poll(series_id, month, key):
    line = {"series": series_id, "present": None, "value": None, "realtime_start": None,
            "last_updated_ny": None, "error": None}
    try:
        # Same settings FredClient sends for a release series (FredClient.cs).
        rows = get("series/observations", key, series_id=series_id, observation_start=month,
                   output_type=4, realtime_start="1776-07-04", realtime_end="9999-12-31")
        row = next((r for r in rows["observations"] if r["date"] == month), None)
        line["present"] = row is not None
        if row:
            line["value"], line["realtime_start"] = row["value"], row["realtime_start"]

        # FRED stamps last_updated in St. Louis time with an offset, e.g. "2026-11-06 09:43:20-06".
        updated = get("series", key, series_id=series_id)["seriess"][0]["last_updated"]
        line["last_updated_ny"] = datetime.fromisoformat(updated).astimezone(NY).isoformat()
    except Exception as error:  # one failed call must not end a once-a-month measurement
        line["error"] = describe(error)
    return line


def at(day, hour_minute):
    return datetime(day.year, day.month, day.day, *hour_minute, tzinfo=NY)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("release", choices=SERIES)
    parser.add_argument("month", help="the month the figure covers, YYYY-MM")
    parser.add_argument("--now", action="store_true", help="skip the wait until 08:25")
    args = parser.parse_args()

    month = f"{args.month}-01"
    key = api_key()
    today = datetime.now(NY)
    release_at, end_at = at(today, RELEASE), at(today, END)
    out = HERE / f"fred_delay-{args.release}-{today:%Y%m%d}.jsonl"

    if not args.now:
        wait = (at(today, START) - datetime.now(NY)).total_seconds()
        if wait > 0:
            print(f"waiting {wait / 60:.0f} min until 08:25 New York time")
            time.sleep(wait)

    first = {}  # series -> the first poll line that had the month's row
    print(f"logging to {out}")
    try:
        with out.open("a") as log:
            next_poll = time.monotonic()
            while datetime.now(NY) < end_at or args.now:
                polled_at = datetime.now(NY)
                for series_id in SERIES[args.release]:
                    line = {"polled_at_ny": polled_at.isoformat(timespec="seconds"),
                            **poll(series_id, month, key)}
                    log.write(json.dumps(line) + "\n")
                    log.flush()
                    print(f"{polled_at:%H:%M:%S} {series_id:<9} present={line['present']} "
                          f"value={line['value']} last_updated={line['last_updated_ny']} "
                          f"error={line['error']}")
                    if line["present"] and series_id not in first:
                        first[series_id] = line
                next_poll += INTERVAL
                time.sleep(max(0, next_poll - time.monotonic()))
    except KeyboardInterrupt:
        print("stopped early")

    print("\nfirst appearance:")
    for series_id in SERIES[args.release]:
        line = first.get(series_id)
        if line is None:
            print(f"  {series_id:<9} never appeared")
            continue
        seen = datetime.fromisoformat(line["polled_at_ny"])
        print(f"  {series_id:<9} seen {seen:%H:%M:%S}, {(seen - release_at) / timedelta(minutes=1):.0f} min "
              f"after 08:30; last_updated then {line['last_updated_ny']}")


if __name__ == "__main__":
    main()
