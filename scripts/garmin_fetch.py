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
import io
import json
import sys
import zipfile
import xml.etree.ElementTree as ET
from datetime import datetime, date, timedelta, timezone
from typing import Any, Dict, Iterable, List, Optional

from garminconnect import Garmin  # type: ignore


def _parse_date(value: Optional[str]) -> Optional[date]:
    if not value:
        return None
    try:
        return datetime.strptime(value, "%Y-%m-%d").date()
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            f"Invalid date '{value}': {exc}") from exc


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


def _millis_to_iso(value: Optional[Any]) -> Optional[str]:
    if value is None:
        return None
    try:
        millis = int(value)
    except (TypeError, ValueError):
        return None

    # Treat the timestamp as Unix epoch milliseconds; Garmin supplies UTC and local variants.
    return datetime.fromtimestamp(millis / 1000, tz=timezone.utc).isoformat().replace("+00:00", "Z")


def _normalise_gmt_timestamp(raw: Optional[str]) -> Optional[str]:
    if not raw:
        return None

    candidate = raw.replace(" ", "T")
    # Garmin sometimes returns fractional seconds suffixed with .0. Remove trailing .0 for isoformat.
    if candidate.endswith(".0"):
        candidate = candidate[:-2]

    try:
        parsed = datetime.fromisoformat(candidate)
    except ValueError:
        # Fallback without fractional seconds
        try:
            parsed = datetime.strptime(candidate, "%Y-%m-%dT%H:%M:%S")
        except ValueError:
            return None

    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    else:
        parsed = parsed.astimezone(timezone.utc)

    return parsed.isoformat().replace("+00:00", "Z")


def _safe_float(value: Any) -> Optional[float]:
    try:
        if value is None:
            return None
        return float(value)
    except (TypeError, ValueError):
        return None


def _extract_trackpoints_from_tcx(raw_bytes: bytes) -> List[Dict[str, Any]]:
    if not raw_bytes:
        return []

    buffer = io.BytesIO(raw_bytes)
    if zipfile.is_zipfile(buffer):
        buffer.seek(0)
        with zipfile.ZipFile(buffer) as archive:
            member_name = next((name for name in archive.namelist()
                                if name.lower().endswith(".tcx")), None)
            if not member_name:
                return []
            xml_bytes = archive.read(member_name)
    else:
        xml_bytes = buffer.getvalue()

    try:
        root = ET.fromstring(xml_bytes)
    except ET.ParseError:
        return []

    trackpoints: List[Dict[str, Any]] = []
    for tp in root.findall(".//{*}Trackpoint"):
        time_el = tp.find("./{*}Time")
        time_value = _normalise_timestamp(
            time_el.text) if time_el is not None else None

        pos_el = tp.find("./{*}Position")
        lat = pos_el.find(
            "./{*}LatitudeDegrees") if pos_el is not None else None
        lon = pos_el.find(
            "./{*}LongitudeDegrees") if pos_el is not None else None

        alt_el = tp.find("./{*}AltitudeMeters")
        distance_el = tp.find("./{*}DistanceMeters")
        hr_el = tp.find("./{*}HeartRateBpm/{*}Value")

        entry: Dict[str, Any] = {
            "timestamp": time_value,
            "latitude": _safe_float(lat.text) if lat is not None else None,
            "longitude": _safe_float(lon.text) if lon is not None else None,
            "altitude": _safe_float(alt_el.text) if alt_el is not None else None,
            "heartRate": _safe_float(hr_el.text) if hr_el is not None else None,
            "distance": _safe_float(distance_el.text) if distance_el is not None else None,
        }

        if entry["timestamp"] is None and entry["latitude"] is None and entry["longitude"] is None:
            continue

        trackpoints.append(entry)

    return trackpoints


def _collect_activity_detail(client: "Garmin", activity_id: str) -> Dict[str, Any]:
    detail: Dict[str, Any] = {}
    errors: Dict[str, str] = {}

    def capture(key: str, callback) -> None:
        try:
            data = callback()
        except Exception as exc:  # pragma: no cover - defensive, depends on API
            errors[key] = str(exc)
        else:
            if data not in (None, {}, []):
                detail[key] = data

    capture("summary", lambda: client.get_activity(activity_id))
    capture("details", lambda: client.get_activity_details(activity_id))
    capture("splitSummaries",
            lambda: client.get_activity_split_summaries(activity_id))
    capture("splits", lambda: client.get_activity_splits(activity_id))
    capture("typedSplits", lambda: client.get_activity_typed_splits(activity_id))
    capture("weather", lambda: client.get_activity_weather(activity_id))
    capture("heartRateZones",
            lambda: client.get_activity_hr_in_timezones(activity_id))
    capture("gear", lambda: client.get_activity_gear(activity_id))
    capture("exerciseSets", lambda: client.get_activity_exercise_sets(activity_id))

    try:
        tcx_bytes = client.download_activity(activity_id)
    except Exception as exc:  # pragma: no cover - defensive
        errors["trackPoints"] = str(exc)
    else:
        trackpoints = _extract_trackpoints_from_tcx(tcx_bytes)
        if trackpoints:
            detail["trackPoints"] = trackpoints

    if errors:
        detail["errors"] = errors

    detail["fetchedAtUtc"] = datetime.utcnow().isoformat() + "Z"
    return detail


def _build_step_entry(source: Dict[str, Any], iso_date: str) -> Dict[str, Any]:
    """Extract step summary fields from a Garmin payload."""

    if not isinstance(source, dict):
        return {}

    def pick(*keys: str) -> Optional[Any]:
        for key in keys:
            if key in source and source[key] is not None:
                return source[key]
        return None

    return {
        "date": pick("calendarDate", "date") or iso_date,
        "totalSteps": pick("totalSteps", "steps", "value"),
        "goal": pick("dailyStepGoal", "goal", "stepGoal"),
        "totalCalories": pick("totalKilocalories", "totalCalories"),
        "activeCalories": pick("activeKilocalories", "activeCalories"),
        "totalDistanceMeters": pick("totalDistanceMeters", "totalDistance", "distanceMeters"),
    }


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fetch Garmin metrics as JSON")
    parser.add_argument("--username", required=True,
                        help="Garmin Connect username")
    parser.add_argument("--password", required=True,
                        help="Garmin Connect password")
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
    parser.add_argument(
        "--steps-only",
        dest="steps_only",
        action="store_true",
        help="Fetch only daily step summaries and skip activity/sleep calls",
    )

    args = parser.parse_args(argv)

    user_start = _parse_date(args.start_date)
    user_end = _parse_date(args.end_date)
    start_date, end_date = _ensure_date_window(user_start, user_end)

    client = Garmin(args.username, args.password)
    client.login()

    activity_payload: List[Dict[str, Any]] = []
    if not args.steps_only:
        try:
            activities = client.get_activities_by_date(
                start_date.isoformat(), end_date.isoformat())
        except Exception:
            # Fallback to index-based retrieval when the date endpoint fails.
            activities = client.get_activities(0, args.max_count)

        for item in activities:
            activity_id = item.get("activityId")
            if activity_id is not None:
                activity_id = str(activity_id)
            detail = _collect_activity_detail(client, activity_id)

            activity_payload.append(
                {
                    "activityId": activity_id,
                    "activityName": item.get("activityName"),
                    "startTime": _normalise_timestamp(item.get("startTimeLocal") or item.get("startTimeGMT")),
                    "distanceMeters": item.get("distance"),
                    "durationSeconds": item.get("duration"),
                    "activityType": (item.get("activityType") or {}).get("typeKey"),
                    "detail": detail,
                }
            )

    step_payload: List[Dict[str, Any]] = []
    step_range_lookup: Dict[str, Dict[str, Any]] = {}

    try:
        range_steps = client.get_daily_steps(
            start_date.isoformat(), end_date.isoformat()
        ) or []
    except Exception:
        range_steps = []

    if isinstance(range_steps, list):
        for item in range_steps:
            if isinstance(item, dict):
                key = item.get("calendarDate") or item.get("date")
                if key:
                    step_range_lookup[key] = item

    for day in _date_range(start_date, end_date):
        iso_date = day.isoformat()

        try:
            stats = client.get_stats_and_body(iso_date) or {}
        except Exception:
            stats = {}

        entry = _build_step_entry(stats, iso_date)

        if not entry.get("totalSteps"):
            fallback = _build_step_entry(
                step_range_lookup.get(iso_date, {}), iso_date)
            for key, value in fallback.items():
                if entry.get(key) in (None, 0) and value not in (None, 0):
                    entry[key] = value

        if not entry.get("totalSteps"):
            try:
                chart = client.get_steps_data(iso_date)
            except Exception:
                chart = []

            if isinstance(chart, list):
                aggregate = {
                    "calendarDate": iso_date,
                    "totalSteps": sum(
                        _safe_float(point.get("steps")) or 0 for point in chart
                        if isinstance(point, dict)
                    ),
                    "totalDistanceMeters": sum(
                        _safe_float(point.get("distanceMeters")) or 0 for point in chart
                        if isinstance(point, dict)
                    ),
                }

                aggregate_entry = _build_step_entry(aggregate, iso_date)
                for key, value in aggregate_entry.items():
                    if entry.get(key) in (None, 0) and value not in (None, 0):
                        entry[key] = value

        if any(value not in (None, 0) for key, value in entry.items() if key != "date"):
            step_payload.append(entry)

    sleep_payload: List[Dict[str, Any]] = []
    sleep_detail_payload: List[Dict[str, Any]] = []
    if not args.steps_only:
        activity_level_stage = {
            0: "Deep",
            1: "Light",
            2: "REM",
            3: "Awake",
        }
        for day in _date_range(start_date, end_date):
            try:
                raw = client.get_sleep_data(day.isoformat()) or {}
            except Exception:
                continue

            summary = (raw.get("dailySleepDTO") or {}
                       ) if isinstance(raw, dict) else {}
            if not summary:
                continue

            detail_levels: List[Dict[str, Any]] = []
            for segment in raw.get("sleepLevels") or []:
                activity_level = segment.get("activityLevel")
                try:
                    stage_key = int(float(activity_level)
                                    ) if activity_level is not None else None
                except (TypeError, ValueError):
                    stage_key = None

                stage = activity_level_stage.get(
                    stage_key) if stage_key is not None else None
                start_utc = _normalise_gmt_timestamp(segment.get("startGMT"))
                end_utc = _normalise_gmt_timestamp(segment.get("endGMT"))

                if stage and start_utc and end_utc:
                    detail_levels.append({
                        "stage": stage,
                        "startUtc": start_utc,
                        "endUtc": end_utc,
                    })

            def _collect_sample(series: Iterable[Dict[str, Any]], *, start_key: str, value_key: str) -> List[Dict[str, Any]]:
                samples: List[Dict[str, Any]] = []
                for item in series:
                    start_value = item.get(start_key)
                    value = _safe_float(item.get(value_key))
                    if value is None:
                        continue

                    timestamp = (
                        _millis_to_iso(start_value)
                        if isinstance(start_value, (int, float, str)) and str(start_value).isdigit()
                        else _normalise_gmt_timestamp(start_value)
                    )
                    if timestamp:
                        samples.append({
                            "timestampUtc": timestamp,
                            "value": value,
                        })
                return samples

            movement_samples: List[Dict[str, Any]] = []
            for segment in raw.get("sleepMovement") or []:
                timestamp = _normalise_gmt_timestamp(segment.get("startGMT"))
                value = _safe_float(segment.get("activityLevel"))
                if timestamp is not None and value is not None:
                    movement_samples.append({
                        "timestampUtc": timestamp,
                        "value": value,
                    })

            heart_rate_samples = _collect_sample(
                raw.get("sleepHeartRate") or [], start_key="startGMT", value_key="value")
            body_battery_samples = _collect_sample(
                raw.get("sleepBodyBattery") or [], start_key="startGMT", value_key="value")

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
                    "sleepStartLocal": _millis_to_iso(summary.get("sleepStartTimestampLocal")),
                    "sleepEndLocal": _millis_to_iso(summary.get("sleepEndTimestampLocal")),
                    "sleepStartGmt": _millis_to_iso(summary.get("sleepStartTimestampGMT")),
                    "sleepEndGmt": _millis_to_iso(summary.get("sleepEndTimestampGMT")),
                    "sleepRestingHeartRate": summary.get("sleepRestingHeartRate"),
                    "bodyBatteryChange": summary.get("bodyBatteryChange"),
                    "averageRespirationValue": summary.get("averageRespirationValue"),
                    "lowestSpO2Value": summary.get("lowestSpO2Value"),
                    "sleepTimeGoalSeconds": summary.get("sleepTimeGoalSeconds"),
                }
            )

            detail_entry = {
                "date": summary.get("calendarDate") or day.isoformat(),
                "levels": detail_levels,
                "movement": movement_samples,
                "heartRate": heart_rate_samples,
                "bodyBattery": body_battery_samples,
            }

            if any(detail_entry[key] for key in ("levels", "movement", "heartRate", "bodyBattery")):
                sleep_detail_payload.append(detail_entry)

    payload = {
        "window": {
            "startDate": start_date.isoformat(),
            "endDate": end_date.isoformat(),
        },
        "activities": activity_payload,
        "steps": step_payload,
        "sleep": sleep_payload,
        "sleepDetails": sleep_detail_payload,
    }

    json.dump(payload, sys.stdout)
    try:
        client.logout()
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
