"""In-memory registry for connected Unity plugin sessions."""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone

import asyncio

from core.config import config
from models.models import ToolDefinitionModel
from transport.runtime_protocol import (
    RELOAD_LIFECYCLE_CAPABILITY,
    RuntimeAdvertisement,
    RuntimeNegotiation,
)


@dataclass(slots=True)
class PluginSession:
    """Represents a single Unity plugin connection."""

    session_id: str
    project_name: str
    project_hash: str
    unity_version: str
    registered_at: datetime
    connected_at: datetime
    tools: dict[str, ToolDefinitionModel] = field(default_factory=dict)
    project_id: str | None = None
    # Full path to project root (for focus nudging)
    project_path: str | None = None
    user_id: str | None = None  # Associated user id (None for local mode)
    runtime: RuntimeAdvertisement | None = None
    runtime_negotiation: RuntimeNegotiation | None = None


@dataclass(slots=True)
class ProjectLifecycle:
    """Last known lifecycle state for a Unity project connection."""

    project_hash: str
    project_name: str
    state: str
    updated_at: datetime
    supports_reload_lifecycle: bool
    session_id: str | None = None
    user_id: str | None = None
    reason: str | None = None


class PluginRegistry:
    """Stores active plugin sessions in-memory.

    The registry is optimised for quick lookup by either ``session_id`` or
    ``project_hash`` (which is used as the canonical "instance id" across the
    HTTP command routing stack).

    In remote-hosted mode, sessions are scoped by (user_id, project_hash) composite key
    to ensure session isolation between users.
    """

    def __init__(self) -> None:
        self._sessions: dict[str, PluginSession] = {}
        # In local mode: project_hash -> session_id
        # In remote mode: (user_id, project_hash) -> session_id
        self._hash_to_session: dict[str, str] = {}
        self._user_hash_to_session: dict[tuple[str, str], str] = {}
        self._lock = asyncio.Lock()
        self._changed = asyncio.Condition(self._lock)
        self._lifecycles: dict[tuple[str | None, str], ProjectLifecycle] = {}

    @staticmethod
    def _lifecycle_key(project_hash: str, user_id: str | None) -> tuple[str | None, str]:
        return user_id, project_hash

    def _session_id_for_hash_unlocked(
        self, project_hash: str, user_id: str | None
    ) -> str | None:
        if user_id:
            return self._user_hash_to_session.get((user_id, project_hash))
        return self._hash_to_session.get(project_hash)

    async def register(
        self,
        session_id: str,
        project_name: str,
        project_hash: str,
        unity_version: str,
        project_path: str | None = None,
        user_id: str | None = None,
        runtime: RuntimeAdvertisement | None = None,
        runtime_negotiation: RuntimeNegotiation | None = None,
    ) -> tuple[PluginSession, str | None]:
        """Register (or replace) a plugin session.

        If an existing session already claims the same ``project_hash`` (and ``user_id``
        in remote-hosted mode) it will be replaced, ensuring that reconnect scenarios
        always map to the latest WebSocket connection.

        Returns:
            A tuple of (new_session, evicted_session_id). The evicted ID is None
            when no previous session was replaced.
        """
        if config.http_remote_hosted and not user_id:
            raise ValueError("user_id is required in remote-hosted mode")

        async with self._changed:
            now = datetime.now(timezone.utc)
            session = PluginSession(
                session_id=session_id,
                project_name=project_name,
                project_hash=project_hash,
                unity_version=unity_version,
                registered_at=now,
                connected_at=now,
                project_path=project_path,
                user_id=user_id,
                runtime=runtime,
                runtime_negotiation=runtime_negotiation,
            )

            # Remove old mapping for this hash if it existed under a different session
            evicted_session_id: str | None = None
            if user_id:
                # Remote-hosted mode: use composite key (user_id, project_hash)
                composite_key = (user_id, project_hash)
                previous_session_id = self._user_hash_to_session.get(
                    composite_key)
                if previous_session_id and previous_session_id != session_id:
                    self._sessions.pop(previous_session_id, None)
                    evicted_session_id = previous_session_id
                self._user_hash_to_session[composite_key] = session_id
            else:
                # Local mode: use project_hash only
                previous_session_id = self._hash_to_session.get(project_hash)
                if previous_session_id and previous_session_id != session_id:
                    self._sessions.pop(previous_session_id, None)
                    evicted_session_id = previous_session_id
                self._hash_to_session[project_hash] = session_id

            self._sessions[session_id] = session
            supports_reload_lifecycle = bool(
                runtime
                and RELOAD_LIFECYCLE_CAPABILITY in runtime.capabilities
            )
            self._lifecycles[self._lifecycle_key(project_hash, user_id)] = ProjectLifecycle(
                project_hash=project_hash,
                project_name=project_name,
                state="ready",
                updated_at=now,
                supports_reload_lifecycle=supports_reload_lifecycle,
                session_id=session_id,
                user_id=user_id,
            )
            self._changed.notify_all()
            return session, evicted_session_id

    async def touch(self, session_id: str) -> None:
        """Update the ``connected_at`` timestamp when a heartbeat is received."""

        async with self._lock:
            session = self._sessions.get(session_id)
            if session:
                session.connected_at = datetime.now(timezone.utc)

    async def unregister(self, session_id: str) -> None:
        """Remove a plugin session from the registry."""

        async with self._changed:
            session = self._sessions.pop(session_id, None)
            if session:
                # Clean up hash mappings
                if session.project_hash in self._hash_to_session:
                    mapped = self._hash_to_session.get(session.project_hash)
                    if mapped == session_id:
                        del self._hash_to_session[session.project_hash]

                # Clean up user-scoped mappings
                if session.user_id:
                    composite_key = (session.user_id, session.project_hash)
                    if composite_key in self._user_hash_to_session:
                        mapped = self._user_hash_to_session.get(composite_key)
                        if mapped == session_id:
                            del self._user_hash_to_session[composite_key]

                key = self._lifecycle_key(session.project_hash, session.user_id)
                lifecycle = self._lifecycles.get(key)
                if lifecycle is None or lifecycle.state != "reloading":
                    supports_reload_lifecycle = bool(
                        session.runtime
                        and RELOAD_LIFECYCLE_CAPABILITY in session.runtime.capabilities
                    )
                    self._lifecycles[key] = ProjectLifecycle(
                        project_hash=session.project_hash,
                        project_name=session.project_name,
                        state="disconnected",
                        updated_at=datetime.now(timezone.utc),
                        supports_reload_lifecycle=supports_reload_lifecycle,
                        user_id=session.user_id,
                    )
                elif lifecycle.session_id == session_id:
                    lifecycle.session_id = None
                    lifecycle.updated_at = datetime.now(timezone.utc)
                self._changed.notify_all()

    async def mark_reloading(
        self, session_id: str, reason: str | None = None
    ) -> ProjectLifecycle | None:
        """Persist an intentional reload marker across WebSocket disconnect."""

        async with self._changed:
            session = self._sessions.get(session_id)
            if session is None:
                return None
            supports_reload_lifecycle = bool(
                session.runtime
                and RELOAD_LIFECYCLE_CAPABILITY in session.runtime.capabilities
            )
            if not supports_reload_lifecycle:
                return None

            lifecycle = ProjectLifecycle(
                project_hash=session.project_hash,
                project_name=session.project_name,
                state="reloading",
                updated_at=datetime.now(timezone.utc),
                supports_reload_lifecycle=True,
                session_id=session_id,
                user_id=session.user_id,
                reason=reason,
            )
            self._lifecycles[
                self._lifecycle_key(session.project_hash, session.user_id)
            ] = lifecycle
            self._changed.notify_all()
            return lifecycle

    async def get_lifecycle(
        self, project_hash: str, user_id: str | None = None
    ) -> ProjectLifecycle | None:
        """Return the last lifecycle signal for a project, including after disconnect."""

        async with self._lock:
            return self._lifecycles.get(self._lifecycle_key(project_hash, user_id))

    async def list_lifecycles(
        self, user_id: str | None = None
    ) -> list[ProjectLifecycle]:
        """Return lifecycle history scoped like active HTTP sessions."""

        if user_id is None and config.http_remote_hosted:
            raise ValueError("list_lifecycles requires user_id in remote-hosted mode")
        async with self._lock:
            return [
                lifecycle
                for lifecycle in self._lifecycles.values()
                if user_id is None or lifecycle.user_id == user_id
            ]

    async def wait_for_session_id_by_hash(
        self,
        project_hash: str,
        user_id: str | None = None,
        timeout: float = 0.0,
    ) -> str | None:
        """Wait without polling until a project session registers or timeout expires."""

        async with self._changed:
            def resolve() -> str | None:
                return self._session_id_for_hash_unlocked(project_hash, user_id)

            session_id = resolve()
            if session_id is not None or timeout <= 0:
                return session_id
            try:
                await asyncio.wait_for(
                    self._changed.wait_for(lambda: resolve() is not None),
                    timeout=timeout,
                )
            except asyncio.TimeoutError:
                return None
            return resolve()

    async def wait_for_available_session(
        self, user_id: str | None = None, timeout: float = 0.0
    ) -> bool:
        """Wait without polling until any selectable session is registered."""

        if user_id is None and config.http_remote_hosted:
            raise ValueError(
                "wait_for_available_session requires user_id in remote-hosted mode"
            )

        async with self._changed:
            def available() -> bool:
                if user_id is None:
                    return bool(self._sessions)
                return any(
                    session.user_id == user_id for session in self._sessions.values()
                )

            if available() or timeout <= 0:
                return available()
            try:
                await asyncio.wait_for(
                    self._changed.wait_for(available),
                    timeout=timeout,
                )
            except asyncio.TimeoutError:
                return False
            return True

    async def register_tools_for_session(self, session_id: str, tools: list[ToolDefinitionModel]) -> None:
        """Register tools for a specific session."""
        async with self._lock:
            session = self._sessions.get(session_id)
            if session:
                # Replace existing tools or merge? Usually replace for "set state".
                # We will replace the dict but keep the field.
                session.tools.clear()
                for tool in tools:
                    session.tools[tool.name] = tool

    async def get_session(self, session_id: str) -> PluginSession | None:
        """Fetch a session by its ``session_id``."""

        async with self._lock:
            return self._sessions.get(session_id)

    async def get_session_id_by_hash(self, project_hash: str, user_id: str | None = None) -> str | None:
        """Resolve a ``project_hash`` (Unity instance id) to a session id."""

        if user_id:
            async with self._lock:
                return self._user_hash_to_session.get((user_id, project_hash))
        else:
            async with self._lock:
                return self._hash_to_session.get(project_hash)

    async def list_sessions(self, user_id: str | None = None) -> dict[str, PluginSession]:
        """Return a shallow copy of sessions.

        Args:
            user_id: If provided, only return sessions for this user (remote-hosted mode).
                     If None, return all sessions (local mode only).

        Raises:
            ValueError: If ``user_id`` is None while running in remote-hosted mode.
                        This prevents accidentally leaking sessions across users.
        """
        if user_id is None and config.http_remote_hosted:
            raise ValueError(
                "list_sessions requires user_id in remote-hosted mode"
            )

        async with self._lock:
            if user_id is None:
                return dict(self._sessions)
            else:
                return {
                    sid: session
                    for sid, session in self._sessions.items()
                    if session.user_id == user_id
                }


__all__ = ["PluginRegistry", "PluginSession", "ProjectLifecycle"]
