from typing import Any, Literal
from pydantic import BaseModel, Field
from models.models import ToolDefinitionModel
from transport.runtime_protocol import RuntimeAdvertisement, RuntimeCommandMetadata

# Outgoing (Server -> Plugin)


class WelcomeMessage(BaseModel):
    type: str = "welcome"
    serverTimeout: int
    keepAliveInterval: int
    runtime: RuntimeAdvertisement | None = None


class RegisteredMessage(BaseModel):
    type: str = "registered"
    session_id: str


class ExecuteCommandMessage(BaseModel):
    type: str = "execute"
    id: str
    name: str
    params: dict[str, Any]
    timeout: float
    runtime: RuntimeCommandMetadata | None = None


class PingMessage(BaseModel):
    """Server-initiated ping to detect dead connections."""
    type: str = "ping"

# Incoming (Plugin -> Server)


class RegisterMessage(BaseModel):
    type: str = "register"
    project_name: str = "Unknown Project"
    project_hash: str
    unity_version: str = "Unknown"
    project_path: str | None = None  # Full path to project root (for focus nudging)
    runtime: RuntimeAdvertisement | None = None


class RegisterToolsMessage(BaseModel):
    type: str = "register_tools"
    tools: list[ToolDefinitionModel]


class PongMessage(BaseModel):
    type: str = "pong"
    session_id: str | None = None


class PluginLifecycleMessage(BaseModel):
    """Intentional Unity transport lifecycle transition."""

    type: Literal["lifecycle"] = "lifecycle"
    state: Literal["reloading"]
    session_id: str | None = None
    reason: str | None = None


class CommandResultMessage(BaseModel):
    type: str = "command_result"
    id: str
    result: dict[str, Any] = Field(default_factory=dict)


class ReceiptStatusResultMessage(BaseModel):
    type: str = "receipt_status_result"
    id: str
    receipt: dict[str, Any] = Field(default_factory=dict)


class CancelRequestResultMessage(BaseModel):
    type: str = "cancel_request_result"
    id: str
    accepted: bool = False
    cancellation_signalled: bool = False
    receipt: dict[str, Any] = Field(default_factory=dict)


class ReceiptLedgerResultMessage(BaseModel):
    type: str = "receipt_ledger_result"
    id: str
    result: dict[str, Any] = Field(default_factory=dict)


# Session Info (API response)


class SessionDetails(BaseModel):
    project: str
    hash: str
    unity_version: str
    connected_at: str


class SessionList(BaseModel):
    sessions: dict[str, SessionDetails]
