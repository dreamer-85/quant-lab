"""The job contract as the Python side sees it.

`fillForward`, `maxObservations` and `gridAnchor` decide what the observation
clock does when data is not evenly spaced, so a typo or a dropped key here would
silently produce a run with different padding than the one the author asked for.
These tests pin the spelling and the defaults, because the C# side cannot see
this layer.
"""

import os
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from quantlab.runner import ResearchJob  # noqa: E402


def _job(**overrides):
    base = dict(
        dataset="crypto",
        symbols=["BTCUSDT"],
        start_time="2022-12-13T00:00:00",
        end_time="2022-12-13T01:00:00",
        experiment_name="dry-run",
    )
    base.update(overrides)
    return ResearchJob(**base)


def test_grid_knobs_round_trip_into_the_job_dict():
    payload = _job(
        fill_forward=False,
        max_observations=500,
        grid_anchor="2022-12-13T00:00:30",
    ).to_job_dict()

    assert payload["fillForward"] is False
    assert payload["maxObservations"] == 500
    assert payload["gridAnchor"] == "2022-12-13T00:00:30"


def test_padding_is_on_by_default_and_the_cap_is_unbounded():
    # The defaults are the engine's defaults: pad to keep periods comparable in
    # wall-clock time, and do not stop on a row count the author did not ask for.
    payload = _job().to_job_dict()

    assert payload["fillForward"] is True
    assert payload["maxObservations"] == 0
    assert payload["gridAnchor"] is None


def test_grid_knobs_change_the_configuration_hash():
    # Resuming a run compares hashes, so a change to the grid has to invalidate a
    # checkpoint rather than silently continuing under different padding rules.
    base = _job()

    assert _job(fill_forward=False).config_hash() != base.config_hash()
    assert _job(max_observations=10).config_hash() != base.config_hash()
    assert _job(grid_anchor="2022-12-13T00:00:30").config_hash() != base.config_hash()


def test_event_driven_mode_still_emits_a_grid_anchor_key():
    # The key is always present, holding null, so consumers do not have to
    # distinguish "absent" from "no anchor" when reading a job dict.
    payload = _job(observation_interval_seconds=None).to_job_dict()

    assert payload["observationInterval"] is None
    assert "gridAnchor" in payload
    assert payload["gridAnchor"] is None
