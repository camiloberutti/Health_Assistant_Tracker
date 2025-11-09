#!/usr/bin/env python3
"""Collect key Garmin Connect metrics using the garminconnect library.

The script emits a JSON document with three top-level arrays: ``activities``,
``sleep`` and ``steps``. Each record is normalised to the fields that the
ASP.NET application persists locally.

Credentials are supplied via command-line arguments. An optional date window
can be provided; otherwise the script fetches the last 7 days (inclusive).
"""
from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, date, timedelta
from typing import Any, Dict, Iterable, List, Optional

from garminconnect import Garmin  # type: ignore


def _parse_date(value: Optional[str]) -> Optional[date]:
    if not value:
        return None
    try:
        return datetime.strptime(value, "%Y-%m-%d").date()
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"Invalid date '{value}': {exc}") from exc


def _normalise_timestamp(raw: Optional[str]) -> Optional[str]:
    if not raw:
        return None
    # Garmin returns either ISO strings or strings with space separator.
    cleaned = raw.replace(" ", "T")
    try:
        # Attempt to parse and re-emit ISO 8601.
        parsed = datetime.fromisoformat(cleaned)
        return parsed.isoformat()
    except ValueError:
        # Fallback: return original string to avoid data loss.
        return cleaned


def _ensure_date_window(start_date: Optional[date], end_date: Optional[date]) -> tuple[date, date]:
    """Return a valid (start, end) date tuple covering at least one day."""

    today = date.today()
    if not start_date and not end_date:
        end_date = today
        start_date = end_date - timedelta(days=6)
    elif start_date and not end_date:
        end_date = start_date
    elif end_date and not start_date:
        start_date = end_date - timedelta(days=6)

    assert start_date is not None and end_date is not None

    if start_date > end_date:
        start_date, end_date = end_date, start_date

    return start_date, end_date


def _date_range(start: date, end: date) -> Iterable[date]:
    for offset in range((end - start).days + 1):
        yield start + timedelta(days=offset)


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Fetch Garmin metrics as JSON")
    parser.add_argument("--username", required=True, help="Garmin Connect username")
    parser.add_argument("--password", required=True, help="Garmin Connect password")
    parser.add_argument(
        "--start-date",
        dest="start_date",
        help="Fetch activities from this date (YYYY-MM-DD)",
    )
    parser.add_argument(
        "--end-date",
        dest="end_date",
        help="Fetch activities up to this date (YYYY-MM-DD)",
    )
    parser.add_argument(
        "--max-count",
        dest="max_count",
        type=int,
        default=50,
        help="Maximum number of activities to fetch when no date range is supplied",
    )

    args = parser.parse_args(argv)

    user_start = _parse_date(args.start_date)
    user_end = _parse_date(args.end_date)
    start_date, end_date = _ensure_date_window(user_start, user_end)

    client = Garmin(args.username, args.password)
    client.login()

    try:
        activities = client.get_activities_by_date(start_date.isoformat(), end_date.isoformat())
    except Exception:
        # Fallback to index-based retrieval when the date endpoint fails.
        activities = client.get_activities(0, args.max_count)

    activity_payload: List[Dict[str, Any]] = []
    for item in activities:
        activity_id = item.get("activityId")
        if activity_id is not None:
            activity_id = str(activity_id)
        activity_payload.append(
            {
                "activityId": activity_id,
                "activityName": item.get("activityName"),
                "startTime": _normalise_timestamp(item.get("startTimeLocal") or item.get("startTimeGMT")),
                "distanceMeters": item.get("distance"),
                "durationSeconds": item.get("duration"),
                "activityType": (item.get("activityType") or {}).get("typeKey"),
            }
        )

    step_payload: List[Dict[str, Any]] = []
    try:
        steps = client.get_daily_steps(start_date.isoformat(), end_date.isoformat()) or []
        for item in steps:
            step_payload.append(
                {
                    "date": item.get("calendarDate"),
                    "totalSteps": item.get("totalSteps"),
                    "goal": item.get("dailyStepGoal"),
                    "totalCalories": item.get("totalKilocalories"),
                    "activeCalories": item.get("activeKilocalories"),
                    "totalDistanceMeters": item.get("totalDistanceMeters"),
                }
            )
    except Exception:
        # Leave steps empty; the caller will handle missing data.
        step_payload = []

    sleep_payload: List[Dict[str, Any]] = []
    for day in _date_range(start_date, end_date):
        try:
            raw = client.get_sleep_data(day.isoformat()) or {}
        except Exception:
            continue

        summary = (raw.get("dailySleepDTO") or {}) if isinstance(raw, dict) else {}
        if not summary:
            continue

        sleep_payload.append(
            {
                "date": summary.get("calendarDate") or day.isoformat(),
                "sleepTimeSeconds": summary.get("sleepTimeSeconds"),
                "deepSleepSeconds": summary.get("deepSleepSeconds"),
                "lightSleepSeconds": summary.get("lightSleepSeconds"),
                "remSleepSeconds": summary.get("remSleepSeconds"),
                "awakeSleepSeconds": summary.get("awakeSleepSeconds"),
                "sleepScore": summary.get("overallSleepScore"),
                "sleepQualityType": summary.get("sleepQualityType"),
            }
        )

    payload = {
        "window": {
            "startDate": start_date.isoformat(),
            "endDate": end_date.isoformat(),
        },
        "activities": activity_payload,
        "steps": step_payload,
        "sleep": sleep_payload,
    }

    json.dump(payload, sys.stdout)
    try:
        client.logout()
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
