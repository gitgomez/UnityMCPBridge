"""Transport-time mutation profile authorization for negotiated Runtime v1 peers."""

from __future__ import annotations

from dataclasses import dataclass
import os
from typing import Any, Mapping

from transport.contract_hash import TOOL_CONTRACTS


MUTATION_PROFILE_ENV = "UNITY_MCP_MUTATION_PROFILE"
DEFAULT_MUTATION_PROFILE = "unrestricted"
MUTATION_PROFILES = frozenset(
    {"read_only", "standard", "destructive", "unrestricted"}
)

_STANDARD_CLASSES = frozenset({"read_only", "editor_state", "scene", "asset"})
_DESTRUCTIVE_CLASSES = frozenset(
    {"read_only", "editor_state", "scene", "asset", "project_settings"}
)

_MANAGE_SCRIPT_ACTION_TO_TOOL = {
    "read": "find_in_file",
    "validate": "validate_script",
    "get_sha": "get_sha",
    "apply_text_edits": "apply_text_edits",
}


@dataclass(frozen=True)
class MutationAuthorization:
    allowed: bool
    profile: str
    tool_name: str
    policy: Mapping[str, Any] | None = None
    code: str | None = None
    message: str | None = None

    def error_data(self) -> dict[str, Any]:
        data: dict[str, Any] = {
            "profile": self.profile,
            "tool_name": self.tool_name,
        }
        if self.policy is not None:
            data.update(
                {
                    "mutation_class": self.policy["mutation_class"],
                    "destructive": self.policy["destructive"],
                }
            )
        return data


def get_server_mutation_profile() -> str:
    """Return the configured profile; absence intentionally preserves old behavior."""

    raw = os.environ.get(MUTATION_PROFILE_ENV)
    return DEFAULT_MUTATION_PROFILE if raw is None else raw.strip().lower()


def resolve_tool_contract(
    command_type: str,
    params: Mapping[str, Any] | None,
) -> tuple[str, Mapping[str, Any] | None]:
    """Resolve a Unity handler invocation back to its public tool contract.

    Most public tools and Unity handlers share a name. ``manage_script`` is the
    sole built-in fan-in handler, so its action disambiguates read-only routes from
    mutation routes. Unknown/custom commands deliberately remain unresolved.
    """

    command_name = (command_type or "").strip()
    if command_name == "manage_script":
        raw_action = params.get("action") if isinstance(params, Mapping) else None
        action = str(raw_action or "").strip().lower()
        tool_name = _MANAGE_SCRIPT_ACTION_TO_TOOL.get(action, "manage_script")
        return tool_name, TOOL_CONTRACTS.get(tool_name)

    direct = TOOL_CONTRACTS.get(command_name)
    if direct is not None and direct.get("handler") == command_name:
        return command_name, direct

    candidates = [
        (name, policy)
        for name, policy in TOOL_CONTRACTS.items()
        if policy.get("requires_unity") and policy.get("handler") == command_name
    ]
    if len(candidates) == 1:
        return candidates[0]
    return command_name, None


def profile_allows(profile: str, policy: Mapping[str, Any] | None) -> bool:
    """Apply the v1 profile lattice to one generated manifest policy."""

    if profile == "unrestricted":
        return True
    if policy is None:
        return False

    mutation_class = policy.get("mutation_class")
    destructive = bool(policy.get("destructive"))
    if profile == "read_only":
        return mutation_class == "read_only" and not destructive
    if profile == "standard":
        return mutation_class in _STANDARD_CLASSES and not destructive
    if profile == "destructive":
        return mutation_class in _DESTRUCTIVE_CLASSES
    return False


def authorize_mutation(
    profile: str,
    command_type: str,
    params: Mapping[str, Any] | None,
) -> MutationAuthorization:
    """Authorize a command and return structured denial information."""

    normalized_profile = (profile or "").strip().lower()
    tool_name, policy = resolve_tool_contract(command_type, params)
    if normalized_profile not in MUTATION_PROFILES:
        return MutationAuthorization(
            allowed=False,
            profile=normalized_profile,
            tool_name=tool_name,
            policy=policy,
            code="INVALID_MUTATION_PROFILE",
            message=(
                f"Unknown mutation profile '{normalized_profile}'. Expected one of: "
                + ", ".join(sorted(MUTATION_PROFILES))
                + "."
            ),
        )

    if policy is None and normalized_profile != "unrestricted":
        return MutationAuthorization(
            allowed=False,
            profile=normalized_profile,
            tool_name=tool_name,
            code="TOOL_CONTRACT_NOT_FOUND",
            message=(
                f"No built-in mutation contract is available for Unity command "
                f"'{command_type}'. Restrictive profiles fail closed."
            ),
        )

    if not profile_allows(normalized_profile, policy):
        return MutationAuthorization(
            allowed=False,
            profile=normalized_profile,
            tool_name=tool_name,
            policy=policy,
            code="MUTATION_PROFILE_DENIED",
            message=(
                f"Mutation profile '{normalized_profile}' does not allow tool "
                f"'{tool_name}'."
            ),
        )

    return MutationAuthorization(
        allowed=True,
        profile=normalized_profile,
        tool_name=tool_name,
        policy=policy,
    )


__all__ = [
    "DEFAULT_MUTATION_PROFILE",
    "MUTATION_PROFILE_ENV",
    "MUTATION_PROFILES",
    "MutationAuthorization",
    "authorize_mutation",
    "get_server_mutation_profile",
    "profile_allows",
    "resolve_tool_contract",
]
