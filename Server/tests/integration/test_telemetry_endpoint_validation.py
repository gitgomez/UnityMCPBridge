import os
import importlib
import pytest


def test_endpoint_rejects_non_http(tmp_path, monkeypatch):
    # Point data dir to temp to avoid touching real files
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))
    monkeypatch.setenv("UNITY_MCP_TELEMETRY_ENDPOINT", "file:///etc/passwd")

    # Import the telemetry module
    telemetry = importlib.import_module("core.telemetry")
    importlib.reload(telemetry)

    tc = telemetry.TelemetryCollector()
    try:
        # Invalid explicit endpoints never fall back to a third-party service.
        assert tc.config.endpoint is None
        assert tc.config.enabled is False
    finally:
        tc.shutdown()


def test_config_preferred_then_env_override(tmp_path, monkeypatch):
    # Simulate config telemetry endpoint
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))
    monkeypatch.delenv("UNITY_MCP_TELEMETRY_ENDPOINT", raising=False)
    for name in ("DISABLE_TELEMETRY", "UNITY_MCP_DISABLE_TELEMETRY", "MCP_DISABLE_TELEMETRY"):
        monkeypatch.delenv(name, raising=False)

    # Patch config.telemetry_endpoint via import mocking
    cfg_mod = importlib.import_module("src.core.config")
    old_endpoint = cfg_mod.config.telemetry_endpoint
    old_enabled = cfg_mod.config.telemetry_enabled
    cfg_mod.config.telemetry_enabled = True
    cfg_mod.config.telemetry_endpoint = "https://example.com/telemetry"
    try:
        telemetry = importlib.import_module("core.telemetry")
        importlib.reload(telemetry)
        tc = telemetry.TelemetryCollector()
        try:
            # When no env override is set, explicitly enabled config is preferred.
            assert tc.config.endpoint == "https://example.com/telemetry"
            assert tc.config.enabled is True
        finally:
            tc.shutdown()

        # Env should override config
        monkeypatch.setenv("UNITY_MCP_TELEMETRY_ENDPOINT",
                           "https://override.example/ep")
        importlib.reload(telemetry)
        tc2 = telemetry.TelemetryCollector()
        try:
            assert tc2.config.endpoint == "https://override.example/ep"
            assert tc2.config.enabled is True
        finally:
            tc2.shutdown()
    finally:
        cfg_mod.config.telemetry_endpoint = old_endpoint
        cfg_mod.config.telemetry_enabled = old_enabled


def test_uuid_preserved_on_malformed_milestones(tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))

    # Import the telemetry module
    telemetry = importlib.import_module("core.telemetry")
    importlib.reload(telemetry)

    tc1 = telemetry.TelemetryCollector()
    first_uuid = tc1._customer_uuid

    # Write malformed milestones
    tc1.config.milestones_file.write_text("{not-json}", encoding="utf-8")

    # Reload collector; UUID should remain same despite bad milestones
    importlib.reload(telemetry)
    tc2 = telemetry.TelemetryCollector()
    assert tc2._customer_uuid == first_uuid
