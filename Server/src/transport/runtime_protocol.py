"""Additive Command Runtime capability advertisement and negotiation."""

from __future__ import annotations

import hashlib
import json
import time
import uuid
from enum import Enum
from typing import Any

from pydantic import BaseModel, Field

from core.telemetry import get_package_version
from transport.contract_hash import BUILT_IN_SCHEMA_HASH, CONTRACT_VERSION


RUNTIME_PROTOCOL = "command-runtime"
RUNTIME_MAJOR = 1
RUNTIME_MINOR = 0
NEGOTIATION_CAPABILITY = "capability_negotiation_v1"
CONTRACT_MANIFEST_CAPABILITY = "contract_manifest_v1"
BOUNDED_COMMAND_QUEUE_CAPABILITY = "bounded_command_queue_v1"
CONTROL_PATH_CAPABILITY = "control_path_v1"
REQUEST_RECEIPTS_CAPABILITY = "request_receipts_v1"
RECEIPT_LEDGER_ADMIN_CAPABILITY = "receipt_ledger_admin_v1"
RESPONSE_ENVELOPE_CAPABILITY = "response_envelope_v1"
MUTATION_CONTRACTS_CAPABILITY = "mutation_contracts_v1"
STABLE_HANDLES_CAPABILITY = "stable_handles_v1"
STATE_REVISIONS_CAPABILITY = "state_revisions_v1"
BATCH_SEMANTICS_CAPABILITY = "batch_semantics_v1"
RELOAD_LIFECYCLE_CAPABILITY = "reload_lifecycle_v1"
MAX_COMMAND_MESSAGE_BYTES = 8 * 1024 * 1024
MAX_RESPONSE_MESSAGE_BYTES = 16 * 1024 * 1024
MAX_QUEUED_COMMANDS = 64
MAX_QUEUED_PAYLOAD_BYTES = 16 * 1024 * 1024
MAX_CONTROL_MESSAGE_BYTES = 64 * 1024
MAX_RECEIPTS = 512
RECEIPT_RETENTION_SECONDS = 30 * 60
MAX_PERSISTED_RECEIPT_BYTES = 8 * 1024 * 1024
MAX_CACHED_RESULT_BYTES = 256 * 1024


class RuntimeMode(str, Enum):
    LEGACY = "legacy"
    RUNTIME_V1 = "runtime_v1"
    DEGRADED = "degraded"
    INCOMPATIBLE = "incompatible"


class RuntimeAdvertisement(BaseModel):
    """Capabilities advertised by one side of the Unity/server connection."""

    protocol: str = RUNTIME_PROTOCOL
    major: int = Field(default=RUNTIME_MAJOR, ge=0)
    minor: int = Field(default=RUNTIME_MINOR, ge=0)
    package_version: str | None = None
    server_version: str | None = None
    contract_version: int | None = Field(default=None, ge=1)
    built_in_schema_hash: str | None = None
    capabilities: list[str] = Field(default_factory=list)
    limits: dict[str, int] = Field(default_factory=dict)
    legacy_fallback: bool = True


class RuntimeNegotiation(BaseModel):
    """Local result derived independently by the server and Unity package."""

    mode: RuntimeMode
    protocol: str | None = None
    major: int | None = None
    minor: int | None = None
    capabilities: list[str] = Field(default_factory=list)
    diagnostics: list[str] = Field(default_factory=list)


class RuntimeCommandMetadata(BaseModel):
    """Retry-stable metadata carried by a Runtime v1 execute attempt."""

    version: int = Field(default=1, ge=1)
    request_id: str
    attempt: int = Field(default=1, ge=1)
    payload_hash: str
    deadline_unix_ms: int
    contract_version: int = Field(default=CONTRACT_VERSION, ge=1)
    tool_name: str | None = None
    profile: str = "unrestricted"
    if_match: dict[str, Any] | None = None


def build_server_advertisement() -> RuntimeAdvertisement:
    """Advertise only features implemented by the current delivery phase."""

    return RuntimeAdvertisement(
        server_version=get_package_version(),
        contract_version=CONTRACT_VERSION,
        built_in_schema_hash=BUILT_IN_SCHEMA_HASH,
        capabilities=[
            NEGOTIATION_CAPABILITY,
            CONTRACT_MANIFEST_CAPABILITY,
            BOUNDED_COMMAND_QUEUE_CAPABILITY,
            CONTROL_PATH_CAPABILITY,
            REQUEST_RECEIPTS_CAPABILITY,
            RECEIPT_LEDGER_ADMIN_CAPABILITY,
            RESPONSE_ENVELOPE_CAPABILITY,
            MUTATION_CONTRACTS_CAPABILITY,
            STABLE_HANDLES_CAPABILITY,
            STATE_REVISIONS_CAPABILITY,
            BATCH_SEMANTICS_CAPABILITY,
            RELOAD_LIFECYCLE_CAPABILITY,
        ],
        limits={
            "max_command_message_bytes": MAX_COMMAND_MESSAGE_BYTES,
            "max_response_message_bytes": MAX_RESPONSE_MESSAGE_BYTES,
            "max_queued_commands": MAX_QUEUED_COMMANDS,
            "max_queued_payload_bytes": MAX_QUEUED_PAYLOAD_BYTES,
            "max_control_message_bytes": MAX_CONTROL_MESSAGE_BYTES,
            "max_receipts": MAX_RECEIPTS,
            "receipt_retention_seconds": RECEIPT_RETENTION_SECONDS,
            "max_persisted_receipt_bytes": MAX_PERSISTED_RECEIPT_BYTES,
            "max_cached_result_bytes": MAX_CACHED_RESULT_BYTES,
        },
    )


def compute_command_payload_hash(
    *,
    name: str,
    params: dict[str, Any],
    project_hash: str,
    contract_version: int = CONTRACT_VERSION,
    tool_name: str | None = None,
    profile: str = "unrestricted",
    if_match: dict[str, Any] | None = None,
) -> str:
    """Hash the logical command payload using the cross-runtime canonical form."""

    payload = {
        "contract_version": contract_version,
        "if_match": if_match,
        "name": tool_name or name,
        "params": params,
        "profile": profile,
        "project_hash": project_hash,
    }
    canonical = json.dumps(
        payload,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")
    return f"sha256:{hashlib.sha256(canonical).hexdigest()}"


def build_runtime_command_metadata(
    *,
    name: str,
    params: dict[str, Any],
    project_hash: str,
    timeout_seconds: float,
    request_id: str | None = None,
    attempt: int = 1,
    tool_name: str | None = None,
    profile: str = "unrestricted",
    if_match: dict[str, Any] | None = None,
) -> RuntimeCommandMetadata:
    """Create metadata once per logical request and reuse its ID on retries."""

    logical_request_id = request_id or str(uuid.uuid4())
    return RuntimeCommandMetadata(
        request_id=logical_request_id,
        attempt=attempt,
        payload_hash=compute_command_payload_hash(
            name=name,
            params=params,
            project_hash=project_hash,
            tool_name=tool_name,
            profile=profile,
            if_match=if_match,
        ),
        deadline_unix_ms=int((time.time() + timeout_seconds) * 1000),
        tool_name=tool_name,
        profile=profile,
        if_match=if_match,
    )


def negotiate_runtime(
    local: RuntimeAdvertisement | None,
    remote: RuntimeAdvertisement | None,
) -> RuntimeNegotiation:
    """Negotiate the safe capability intersection without breaking legacy peers."""

    if local is None or remote is None:
        return RuntimeNegotiation(
            mode=RuntimeMode.LEGACY,
            diagnostics=["runtime advertisement missing on one side"],
        )

    if local.protocol != remote.protocol:
        if local.legacy_fallback or remote.legacy_fallback:
            return RuntimeNegotiation(
                mode=RuntimeMode.LEGACY,
                diagnostics=["runtime protocol mismatch; using legacy fallback"],
            )
        return RuntimeNegotiation(
            mode=RuntimeMode.INCOMPATIBLE,
            diagnostics=["runtime protocol mismatch without legacy fallback"],
        )

    if local.major != remote.major:
        if local.legacy_fallback or remote.legacy_fallback:
            return RuntimeNegotiation(
                mode=RuntimeMode.LEGACY,
                protocol=local.protocol,
                diagnostics=["runtime major version mismatch; using legacy fallback"],
            )
        return RuntimeNegotiation(
            mode=RuntimeMode.INCOMPATIBLE,
            protocol=local.protocol,
            diagnostics=["runtime major version mismatch without legacy fallback"],
        )

    capabilities = sorted(set(local.capabilities) & set(remote.capabilities))
    diagnostics: list[str] = []
    mode = RuntimeMode.RUNTIME_V1

    if not capabilities:
        mode = RuntimeMode.DEGRADED
        diagnostics.append("no runtime capabilities were negotiated")

    if (
        local.built_in_schema_hash
        and remote.built_in_schema_hash
        and local.built_in_schema_hash != remote.built_in_schema_hash
    ):
        mode = RuntimeMode.DEGRADED
        diagnostics.append("built-in tool schema hash mismatch")

    return RuntimeNegotiation(
        mode=mode,
        protocol=local.protocol,
        major=local.major,
        minor=min(local.minor, remote.minor),
        capabilities=capabilities,
        diagnostics=diagnostics,
    )


__all__ = [
    "NEGOTIATION_CAPABILITY",
    "CONTRACT_MANIFEST_CAPABILITY",
    "BOUNDED_COMMAND_QUEUE_CAPABILITY",
    "CONTROL_PATH_CAPABILITY",
    "REQUEST_RECEIPTS_CAPABILITY",
    "RECEIPT_LEDGER_ADMIN_CAPABILITY",
    "RESPONSE_ENVELOPE_CAPABILITY",
    "MUTATION_CONTRACTS_CAPABILITY",
    "STABLE_HANDLES_CAPABILITY",
    "STATE_REVISIONS_CAPABILITY",
    "BATCH_SEMANTICS_CAPABILITY",
    "MAX_COMMAND_MESSAGE_BYTES",
    "MAX_CONTROL_MESSAGE_BYTES",
    "MAX_CACHED_RESULT_BYTES",
    "MAX_PERSISTED_RECEIPT_BYTES",
    "MAX_QUEUED_COMMANDS",
    "MAX_QUEUED_PAYLOAD_BYTES",
    "MAX_RECEIPTS",
    "MAX_RESPONSE_MESSAGE_BYTES",
    "RECEIPT_RETENTION_SECONDS",
    "RUNTIME_MAJOR",
    "RUNTIME_MINOR",
    "RUNTIME_PROTOCOL",
    "RuntimeAdvertisement",
    "RuntimeCommandMetadata",
    "RuntimeMode",
    "RuntimeNegotiation",
    "build_server_advertisement",
    "build_runtime_command_metadata",
    "compute_command_payload_hash",
    "negotiate_runtime",
]
