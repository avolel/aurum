#!/usr/bin/env python3
"""Phase 2 item 1: how long after BLS's 8:30 release do BLS and FRED post the figure? (D-22, D-23)

Run by hand on release morning, before 08:25 New York time:
    python3 ops/bench/fred_delay.py cpi 2026-10      # October CPI
    python3 ops/bench/fred_delay.py jobs 2026-11     # November jobs report
--now skips the wait for a test run; Ctrl-C stops early and still prints the summary.
A test run appends to the same dated file as the real run, so test on another day.

Polls every 60 seconds until 11:00 and writes one JSON line per series per poll.
Standard library only. Reads Macro__Fred__ApiKey and Macro__Bls__ApiKey from the repo's .env.
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
BLS = "https://api.bls.gov/publicAPI/v2/"
NY = ZoneInfo("America/New_York")
# Paired by position: BLS_SERIES[r][i] is the BLS series FRED_SERIES[r][i] copies (BlsSeries.cs).
FRED_SERIES = {"cpi": ["CPIAUCSL", "CPILFESL"], "jobs": ["PAYEMS", "UNRATE"]}
BLS_SERIES = {"cpi": ["CUSR0000SA0", "CUSR0000SA0L1E"], "jobs": ["CES0000000001", "LNS14000000"]}
START, RELEASE, END = (8, 25), (8, 30), (11, 0)
# Each round: 4 FRED requests (FRED allows 120 a minute) and 1 BLS request
# (about 155 a morning against a registered key's 500 a day).
INTERVAL = 60

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]


def api_key(name):
    for line in (REPO / ".env").read_text().splitlines():
        setting, sep, value = line.partition("=")
        if sep and setting.strip() == name:
            return value.strip().strip("\"'")
    raise SystemExit(f"{name} is not in .env")


def fred_get(path, key, **params):
    # The key is in the query string, so the URL is never logged or printed.
    query = urllib.parse.urlencode({**params, "api_key": key, "file_type": "json"})
    with urllib.request.urlopen(f"{FRED}{path}?{query}", timeout=20) as resp:
        return json.load(resp)


def bls_post(key, **body):
    # Same request BlsClient.cs sends: the key goes in the body, lowercase "registrationkey".
    data = json.dumps({**body, "registrationkey": key}).encode()
    req = urllib.request.Request(f"{BLS}timeseries/data/", data=data,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=20) as resp:
        # BLS copies the key into its refusal message, so scrub it before anything reads it.
        answer = json.loads(resp.read().decode().replace(key, "[key]"))
    # BLS answers 200 with a refusal in the body (bad key, daily limit reached).
    if answer.get("status") != "REQUEST_SUCCEEDED":
        raise RuntimeError(f"BLS {answer.get('status')}: {' '.join(answer.get('message') or [])}")
    return answer


def describe(error):
    if isinstance(error, urllib.error.HTTPError):
        try:
            message = json.load(error).get("error_message", "")
        except Exception:
            message = ""
        return f"HTTP {error.code} {message}".strip()
    return f"{type(error).__name__}: {error}"  # URLError and timeouts carry no URL


def fred_poll(series_id, month, key):
    line = {"source": "fred", "series": series_id, "present": None, "value": None,
            "realtime_start": None, "last_updated_ny": None, "error": None}
    try:
        # Same settings FredClient sends for a release series (FredClient.cs).
        rows = fred_get("series/observations", key, series_id=series_id, observation_start=month,
                        output_type=4, realtime_start="1776-07-04", realtime_end="9999-12-31")
        row = next((r for r in rows["observations"] if r["date"] == month), None)
        line["present"] = row is not None
        if row:
            line["value"], line["realtime_start"] = row["value"], row["realtime_start"]

        # FRED stamps last_updated in St. Louis time with an offset, e.g. "2026-11-06 09:43:20-06".
        updated = fred_get("series", key, series_id=series_id)["seriess"][0]["last_updated"]
        line["last_updated_ny"] = datetime.fromisoformat(updated).astimezone(NY).isoformat()
    except Exception as error:  # one failed call must not end a once-a-month measurement
        line["error"] = describe(error)
    return line


def bls_poll(series_ids, month, key):
    """One request for every series, so a round spends one query of the daily limit."""
    lines = [{"source": "bls", "series": s, "present": None, "value": None, "error": None}
             for s in series_ids]
    year, period = month[:4], f"M{month[5:7]}"  # "2026-10-01" -> "2026", "M10"
    try:
        answer = bls_post(key, seriesid=series_ids, startyear=year, endyear=year)
        data = {s["seriesID"]: s["data"] for s in answer["Results"]["series"]}
        for line in lines:
            row = next((d for d in data.get(line["series"], [])
                        if d["year"] == year and d["period"] == period), None)
            line["present"] = row is not None and row["value"] != "-"  # "-" means not available
            if line["present"]:
                line["value"] = row["value"]
    except Exception as error:  # one failed call must not end a once-a-month measurement
        for line in lines:
            line["error"] = describe(error)
    return lines


def at(day, hour_minute):
    return datetime(day.year, day.month, day.day, *hour_minute, tzinfo=NY)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("release", choices=FRED_SERIES)
    parser.add_argument("month", help="the month the figure covers, YYYY-MM")
    parser.add_argument("--now", action="store_true", help="skip the wait until 08:25")
    args = parser.parse_args()

    fred_ids, bls_ids = FRED_SERIES[args.release], BLS_SERIES[args.release]
    month = f"{args.month}-01"
    fred_key, bls_key = api_key("Macro__Fred__ApiKey"), api_key("Macro__Bls__ApiKey")
    today = datetime.now(NY)
    release_at, end_at = at(today, RELEASE), at(today, END)
    out = HERE / f"fred_delay-{args.release}-{today:%Y%m%d}.jsonl"

    if not args.now:
        wait = (at(today, START) - datetime.now(NY)).total_seconds()
        if wait > 0:
            print(f"waiting {wait / 60:.0f} min until 08:25 New York time")
            time.sleep(wait)

    first = {}  # series id -> the first poll line that had the month's row
    print(f"logging to {out}")
    try:
        with out.open("a") as log:
            next_poll = time.monotonic()
            while datetime.now(NY) < end_at or args.now:
                polled_at = datetime.now(NY)
                lines = [fred_poll(s, month, fred_key) for s in fred_ids]
                lines += bls_poll(bls_ids, month, bls_key)
                for line in lines:
                    line = {"polled_at_ny": polled_at.isoformat(timespec="seconds"), **line}
                    log.write(json.dumps(line) + "\n")
                    log.flush()
                    print(f"{polled_at:%H:%M:%S} {line['source']:<4} {line['series']:<14} "
                          f"present={line['present']} value={line['value']} "
                          f"last_updated={line.get('last_updated_ny')} error={line['error']}")
                    if line["present"] and line["series"] not in first:
                        first[line["series"]] = line
                next_poll += INTERVAL
                time.sleep(max(0, next_poll - time.monotonic()))
    except KeyboardInterrupt:
        print("stopped early")

    def seen(series_id):
        line = first.get(series_id)
        return datetime.fromisoformat(line["polled_at_ny"]) if line else None

    def minutes(delta):
        return f"{delta / timedelta(minutes=1):.0f} min"

    print("\nfirst appearance:")
    for fred_id, bls_id in zip(fred_ids, bls_ids):
        fred_seen, bls_seen = seen(fred_id), seen(bls_id)
        for label, series_id, when in (("FRED", fred_id, fred_seen), ("BLS", bls_id, bls_seen)):
            if when is None:
                print(f"  {label:<4} {series_id:<14} never appeared")
            else:
                print(f"  {label:<4} {series_id:<14} seen {when:%H:%M:%S}, "
                      f"{minutes(when - release_at)} after 08:30")
        if fred_seen and bls_seen:
            print(f"  FRED trailed BLS by {minutes(fred_seen - bls_seen)}")
        print(f"  FRED last_updated then {first.get(fred_id, {}).get('last_updated_ny')}\n")


if __name__ == "__main__":
    main()
