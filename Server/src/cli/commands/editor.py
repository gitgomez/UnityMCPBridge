"""Editor CLI commands."""

import sys
import click
from typing import Optional, Any

from cli.utils.config import get_config
from cli.utils.output import format_output, print_error, print_success, print_info
from cli.utils.connection import run_command, run_list_custom_tools, handle_unity_errors, UnityConnectionError
from cli.utils.suggestions import suggest_matches, format_suggestions
from cli.utils.parsers import parse_json_dict_or_exit


@click.group()
def editor():
    """Editor operations - play mode, console, tags, layers."""
    pass


def _runtime_ui_address_params(
    *,
    action: str,
    ui_system: str,
    target: Optional[str],
    position: Optional[tuple[float, float]],
    search_method: Optional[str],
    document: Optional[str],
    document_search_method: Optional[str],
    element_name: Optional[str],
    element_class: Optional[str],
    element_type: Optional[str],
    element_index: Optional[int],
) -> dict[str, Any]:
    params: dict[str, Any] = {"action": action}
    if ui_system == "ugui":
        if any(
            value is not None
            for value in (
                document,
                document_search_method,
                element_name,
                element_class,
                element_type,
                element_index,
            )
        ):
            raise click.UsageError(
                "--document and --element-* require --ui-system ui_toolkit."
            )
        if (target is None) == (position is None):
            raise click.UsageError(
                "Provide exactly one TARGET or --position X Y."
            )
        if search_method is not None and target is None:
            raise click.UsageError(
                "--search-method can only be used with TARGET."
            )
        if target is not None:
            params["target"] = target
        if position is not None:
            params["position"] = list(position)
        if search_method is not None:
            params["search_method"] = search_method
        return params

    if target is not None or search_method is not None:
        raise click.UsageError(
            "TARGET and --search-method are only valid for --ui-system ugui."
        )
    if document is None or not document.strip():
        raise click.UsageError(
            "--document is required for --ui-system ui_toolkit."
        )
    has_query = any(
        value is not None
        for value in (element_name, element_class, element_type)
    )
    if has_query == (position is not None):
        raise click.UsageError(
            "Provide exactly one UI Toolkit element query or --position X Y."
        )
    if element_index is not None and not has_query:
        raise click.UsageError(
            "--element-index requires --element-name, --element-class, or --element-type."
        )

    params.update(
        {
            "ui_system": "ui_toolkit",
            "document": document,
        }
    )
    if document_search_method is not None:
        params["document_search_method"] = document_search_method
    if element_name is not None:
        params["element_name"] = element_name
    if element_class is not None:
        params["element_class"] = element_class
    if element_type is not None:
        params["element_type"] = element_type
    if element_index is not None:
        params["element_index"] = element_index
    if position is not None:
        params["position"] = list(position)
    return params


def _runtime_ui_element_params(
    *,
    action: str,
    ui_system: str,
    target: Optional[str],
    search_method: Optional[str],
    document: Optional[str],
    document_search_method: Optional[str],
    element_name: Optional[str],
    element_class: Optional[str],
    element_type: Optional[str],
    element_index: Optional[int],
) -> dict[str, Any]:
    """Build an element-only uGUI/UI Toolkit address for stateful actions."""
    params: dict[str, Any] = {"action": action}
    if ui_system == "ugui":
        if any(
            value is not None
            for value in (
                document,
                document_search_method,
                element_name,
                element_class,
                element_type,
                element_index,
            )
        ):
            raise click.UsageError(
                "--document and --element-* require --ui-system ui_toolkit."
            )
        if target is None or not target.strip():
            raise click.UsageError(
                "TARGET is required for --ui-system ugui."
            )
        params["target"] = target
        if search_method is not None:
            params["search_method"] = search_method
        return params

    if target is not None or search_method is not None:
        raise click.UsageError(
            "TARGET and --search-method are only valid for --ui-system ugui."
        )
    if document is None or not document.strip():
        raise click.UsageError(
            "--document is required for --ui-system ui_toolkit."
        )
    has_query = any(
        value is not None
        for value in (element_name, element_class, element_type)
    )
    if not has_query:
        raise click.UsageError(
            "Provide --element-name, --element-class, or --element-type."
        )
    if element_index is not None and not has_query:
        raise click.UsageError(
            "--element-index requires --element-name, --element-class, or --element-type."
        )

    params.update(
        {
            "ui_system": "ui_toolkit",
            "document": document,
        }
    )
    if document_search_method is not None:
        params["document_search_method"] = document_search_method
    if element_name is not None:
        params["element_name"] = element_name
    if element_class is not None:
        params["element_class"] = element_class
    if element_type is not None:
        params["element_type"] = element_type
    if element_index is not None:
        params["element_index"] = element_index
    return params


@editor.command("play")
@handle_unity_errors
def play():
    """Enter play mode."""
    config = get_config()
    result = run_command("manage_editor", {"action": "play"}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Entered play mode")


@editor.command("pause")
@handle_unity_errors
def pause():
    """Pause play mode."""
    config = get_config()
    result = run_command("manage_editor", {"action": "pause"}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Paused play mode")


@editor.command("stop")
@handle_unity_errors
def stop():
    """Stop play mode."""
    config = get_config()
    result = run_command("manage_editor", {"action": "stop"}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Stopped play mode")


@editor.command("play-ui-status")
@handle_unity_errors
def play_ui_status():
    """Report runtime uGUI/UI Toolkit support and Play Mode state."""
    config = get_config()
    result = run_command(
        "interact_play_mode",
        {"action": "ping"},
        config,
    )
    click.echo(format_output(result, config.format))


@editor.command("inspect-ui")
@click.argument("target", required=False)
@click.option(
    "--ui-system",
    type=click.Choice(["ugui", "ui_toolkit"]),
    default="ugui",
    show_default=True,
)
@click.option(
    "--search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--document", default=None, help="UIDocument GameObject for UI Toolkit.")
@click.option(
    "--document-search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--element-name", default=None, help="Exact VisualElement name.")
@click.option("--element-class", default=None, help="Required USS class.")
@click.option("--element-type", default=None, help="Exact VisualElement type.")
@click.option("--element-index", type=click.IntRange(0, 1023), default=None)
@click.option(
    "--include-text/--no-include-text",
    default=True,
    show_default=True,
    help="Include non-sensitive text in the inspection.",
)
@handle_unity_errors
def inspect_ui(
    target: Optional[str],
    ui_system: str,
    search_method: Optional[str],
    document: Optional[str],
    document_search_method: Optional[str],
    element_name: Optional[str],
    element_class: Optional[str],
    element_type: Optional[str],
    element_index: Optional[int],
    include_text: bool,
):
    """Inspect one runtime uGUI or UI Toolkit element."""
    params = _runtime_ui_element_params(
        action="inspect_ui",
        ui_system=ui_system,
        target=target,
        search_method=search_method,
        document=document,
        document_search_method=document_search_method,
        element_name=element_name,
        element_class=element_class,
        element_type=element_type,
        element_index=element_index,
    )
    params["include_text"] = include_text

    config = get_config()
    result = run_command("interact_play_mode", params, config)
    click.echo(format_output(result, config.format))


@editor.command("wait-ui")
@click.argument("target", required=False)
@click.option(
    "--ui-system",
    type=click.Choice(["ugui", "ui_toolkit"]),
    default="ugui",
    show_default=True,
)
@click.option(
    "--search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--document", default=None, help="UIDocument GameObject for UI Toolkit.")
@click.option(
    "--document-search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--element-name", default=None, help="Exact VisualElement name.")
@click.option("--element-class", default=None, help="Required USS class.")
@click.option("--element-type", default=None, help="Exact VisualElement type.")
@click.option("--element-index", type=click.IntRange(0, 1023), default=None)
@click.option(
    "--condition",
    type=click.Choice(
        [
            "exists",
            "active",
            "visible",
            "interactable",
            "selected",
            "text_equals",
            "text_contains",
            "toggle_equals",
            "hovered",
        ]
    ),
    required=True,
)
@click.option(
    "--expected",
    default=None,
    help="Boolean true/false or expected text; boolean conditions default to true.",
)
@click.option(
    "--timeout",
    "timeout_seconds",
    type=click.FloatRange(0.1, 30.0),
    default=5.0,
    show_default=True,
)
@click.option(
    "--poll-interval",
    "poll_interval_seconds",
    type=click.FloatRange(0.05, 1.0),
    default=0.1,
    show_default=True,
)
@click.option(
    "--include-text/--no-include-text",
    default=True,
    show_default=True,
)
@handle_unity_errors
def wait_ui(
    target: Optional[str],
    ui_system: str,
    search_method: Optional[str],
    document: Optional[str],
    document_search_method: Optional[str],
    element_name: Optional[str],
    element_class: Optional[str],
    element_type: Optional[str],
    element_index: Optional[int],
    condition: str,
    expected: Optional[str],
    timeout_seconds: float,
    poll_interval_seconds: float,
    include_text: bool,
):
    """Wait for one bounded runtime UI condition."""
    if condition == "hovered" and ui_system != "ui_toolkit":
        raise click.UsageError("The hovered condition requires --ui-system ui_toolkit.")
    text_condition = condition in {"text_equals", "text_contains"}
    if text_condition:
        if expected is None:
            raise click.UsageError(
                "--expected is required for text conditions."
            )
        resolved_expected: bool | str = expected
    elif expected is None:
        resolved_expected = True
    elif expected.lower() in {"true", "false"}:
        resolved_expected = expected.lower() == "true"
    else:
        raise click.UsageError(
            "--expected must be true or false for boolean conditions."
        )

    params = _runtime_ui_element_params(
        action="wait_ui",
        ui_system=ui_system,
        target=target,
        search_method=search_method,
        document=document,
        document_search_method=document_search_method,
        element_name=element_name,
        element_class=element_class,
        element_type=element_type,
        element_index=element_index,
    )
    params.update(
        {
            "condition": condition,
            "expected": resolved_expected,
            "timeout_seconds": timeout_seconds,
            "poll_interval_seconds": poll_interval_seconds,
            "include_text": include_text,
        }
    )
    config = get_config()
    result = run_command("interact_play_mode", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Runtime UI condition satisfied")


@editor.command("set-ui-text")
@click.argument("target", required=False)
@click.option("--text", required=True, help="New input-field value.")
@click.option(
    "--ui-system",
    type=click.Choice(["ugui", "ui_toolkit"]),
    default="ugui",
    show_default=True,
)
@click.option(
    "--search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--document", default=None, help="UIDocument GameObject for UI Toolkit.")
@click.option(
    "--document-search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--element-name", default=None, help="Exact VisualElement name.")
@click.option("--element-class", default=None, help="Required USS class.")
@click.option("--element-type", default=None, help="Exact VisualElement type.")
@click.option("--element-index", type=click.IntRange(0, 1023), default=None)
@click.option("--submit", is_flag=True, help="Dispatch submit after updating text.")
@click.option(
    "--sensitive",
    is_flag=True,
    help="Prevent text lengths and values from being exposed.",
)
@handle_unity_errors
def set_ui_text(
    target: Optional[str],
    text: str,
    ui_system: str,
    search_method: Optional[str],
    document: Optional[str],
    document_search_method: Optional[str],
    element_name: Optional[str],
    element_class: Optional[str],
    element_type: Optional[str],
    element_index: Optional[int],
    submit: bool,
    sensitive: bool,
):
    """Set one runtime uGUI or UI Toolkit input value."""
    params = _runtime_ui_element_params(
        action="set_text",
        ui_system=ui_system,
        target=target,
        search_method=search_method,
        document=document,
        document_search_method=document_search_method,
        element_name=element_name,
        element_class=element_class,
        element_type=element_type,
        element_index=element_index,
    )
    params.update(
        {
            "text": text,
            "submit": submit,
            "sensitive": sensitive,
        }
    )

    config = get_config()
    result = run_command("interact_play_mode", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Runtime UI text updated")


@editor.command("set-ui-toggle")
@click.argument("target", required=False)
@click.option(
    "--value",
    type=click.Choice(["true", "false"]),
    required=True,
)
@click.option(
    "--ui-system",
    type=click.Choice(["ugui", "ui_toolkit"]),
    default="ugui",
    show_default=True,
)
@click.option(
    "--search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--document", default=None, help="UIDocument GameObject for UI Toolkit.")
@click.option(
    "--document-search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--element-name", default=None, help="Exact VisualElement name.")
@click.option("--element-class", default=None, help="Required USS class.")
@click.option("--element-type", default=None, help="Exact VisualElement type.")
@click.option("--element-index", type=click.IntRange(0, 1023), default=None)
@handle_unity_errors
def set_ui_toggle(
    target: Optional[str],
    value: str,
    ui_system: str,
    search_method: Optional[str],
    document: Optional[str],
    document_search_method: Optional[str],
    element_name: Optional[str],
    element_class: Optional[str],
    element_type: Optional[str],
    element_index: Optional[int],
):
    """Set one runtime uGUI or UI Toolkit toggle value."""
    params = _runtime_ui_element_params(
        action="set_toggle",
        ui_system=ui_system,
        target=target,
        search_method=search_method,
        document=document,
        document_search_method=document_search_method,
        element_name=element_name,
        element_class=element_class,
        element_type=element_type,
        element_index=element_index,
    )
    params["value"] = value == "true"

    config = get_config()
    result = run_command("interact_play_mode", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Runtime UI toggle updated")


@editor.command("click-ui")
@click.argument("target", required=False)
@click.option(
    "--ui-system",
    type=click.Choice(["ugui", "ui_toolkit"]),
    default="ugui",
    show_default=True,
)
@click.option(
    "--position",
    nargs=2,
    type=click.FloatRange(0.0, 1.0),
    default=None,
    metavar="X Y",
    help="Normalized Game View coordinates with a top-left origin.",
)
@click.option(
    "--search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
    help="Target lookup mode; inferred when omitted.",
)
@click.option("--document", default=None, help="UIDocument GameObject for UI Toolkit.")
@click.option(
    "--document-search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--element-name", default=None, help="Exact VisualElement name.")
@click.option("--element-class", default=None, help="Required USS class.")
@click.option("--element-type", default=None, help="Exact VisualElement type.")
@click.option("--element-index", type=click.IntRange(0, 1023), default=None)
@handle_unity_errors
def click_ui(
    target: Optional[str],
    ui_system: str,
    position: Optional[tuple[float, float]],
    search_method: Optional[str],
    document: Optional[str],
    document_search_method: Optional[str],
    element_name: Optional[str],
    element_class: Optional[str],
    element_type: Optional[str],
    element_index: Optional[int],
):
    """Click runtime uGUI or UI Toolkit.

    Explicit targets can be validated before their Canvas' first render. Coordinate
    clicks always require a normal EventSystem raycast.

    \b
    Examples:
        unity-mcp editor click-ui "Canvas/Menu/Start" --search-method by_path
        unity-mcp editor click-ui --position 0.5 0.75
        unity-mcp editor click-ui --ui-system ui_toolkit --document UIRoot --element-name start-button
    """
    params = _runtime_ui_address_params(
        action="click_ui",
        ui_system=ui_system,
        target=target,
        position=position,
        search_method=search_method,
        document=document,
        document_search_method=document_search_method,
        element_name=element_name,
        element_class=element_class,
        element_type=element_type,
        element_index=element_index,
    )

    config = get_config()
    result = run_command("interact_play_mode", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Runtime UI click dispatched")


@editor.command("hover-ui")
@click.option("--document", required=True, help="Runtime UIDocument GameObject.")
@click.option("--document-search-method", type=click.Choice(["by_id", "by_name", "by_path"]), default=None)
@click.option("--element-name", default=None)
@click.option("--element-class", default=None)
@click.option("--element-type", default=None)
@click.option("--element-index", type=click.IntRange(0, 1023), default=None)
@click.option("--position", nargs=2, type=click.FloatRange(0.0, 1.0), default=None, metavar="X Y")
@handle_unity_errors
def hover_ui(document, document_search_method, element_name, element_class, element_type, element_index, position):
    """Move the UI Toolkit pointer without pressing or releasing a button."""
    params = _runtime_ui_address_params(
        action="hover_ui", ui_system="ui_toolkit", target=None, search_method=None,
        document=document, document_search_method=document_search_method,
        element_name=element_name, element_class=element_class,
        element_type=element_type, element_index=element_index, position=position,
    )
    config = get_config()
    result = run_command("interact_play_mode", params, config)
    click.echo(format_output(result, config.format))


@editor.command("key-ui")
@click.option("--document", required=True, help="Runtime UIDocument; current focus or root receives the key pair.")
@click.option("--document-search-method", type=click.Choice(["by_id", "by_name", "by_path"]), default=None)
@click.option("--key-code", required=True, type=click.Choice([
    "Escape", "Tab", "Return", "Space",
    "LeftArrow", "RightArrow", "UpArrow", "DownArrow",
    "Backspace", "Delete", "Home", "End", "PageUp", "PageDown",
]))
@click.option("--modifier", "modifiers", multiple=True, type=click.Choice(["Shift", "Control", "Alt", "Command"]))
@handle_unity_errors
def key_ui(document, document_search_method, key_code, modifiers):
    """Send a UI Toolkit KeyDown/KeyUp pair. Tab/Return do not generate navigation/submit. No text or device input."""
    if not document.strip():
        raise click.UsageError("--document cannot be blank.")
    if len(modifiers) != len(set(modifiers)):
        raise click.UsageError("--modifier values must be distinct.")
    params = {
        "action": "key_ui", "ui_system": "ui_toolkit",
        "document": document, "key_code": key_code,
    }
    if document_search_method is not None:
        params["document_search_method"] = document_search_method
    if modifiers:
        params["modifiers"] = list(modifiers)
    config = get_config()
    result = run_command("interact_play_mode", params, config)
    click.echo(format_output(result, config.format))


@editor.command("drag-ui")
@click.argument("target", required=False)
@click.option(
    "--ui-system",
    type=click.Choice(["ugui", "ui_toolkit"]),
    default="ugui",
    show_default=True,
)
@click.option(
    "--position",
    nargs=2,
    type=click.FloatRange(0.0, 1.0),
    default=None,
    metavar="X Y",
    help="Normalized top-left-origin drag start coordinates.",
)
@click.option(
    "--end-position",
    nargs=2,
    type=click.FloatRange(0.0, 1.0),
    required=True,
    metavar="X Y",
    help="Normalized top-left-origin drag destination.",
)
@click.option(
    "--steps",
    type=click.IntRange(1, 64),
    default=5,
    show_default=True,
    help="Number of synchronous pointer movement steps.",
)
@click.option(
    "--search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
    help="Target lookup mode; inferred when omitted.",
)
@click.option("--document", default=None, help="UIDocument GameObject for UI Toolkit.")
@click.option(
    "--document-search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--element-name", default=None, help="Exact VisualElement name.")
@click.option("--element-class", default=None, help="Required USS class.")
@click.option("--element-type", default=None, help="Exact VisualElement type.")
@click.option("--element-index", type=click.IntRange(0, 1023), default=None)
@handle_unity_errors
def drag_ui(
    target: Optional[str],
    ui_system: str,
    position: Optional[tuple[float, float]],
    end_position: tuple[float, float],
    steps: int,
    search_method: Optional[str],
    document: Optional[str],
    document_search_method: Optional[str],
    element_name: Optional[str],
    element_class: Optional[str],
    element_type: Optional[str],
    element_index: Optional[int],
):
    """Drag runtime uGUI or UI Toolkit to END_POSITION."""
    params = _runtime_ui_address_params(
        action="drag_ui",
        ui_system=ui_system,
        target=target,
        position=position,
        search_method=search_method,
        document=document,
        document_search_method=document_search_method,
        element_name=element_name,
        element_class=element_class,
        element_type=element_type,
        element_index=element_index,
    )
    params.update(
        {
            "end_position": list(end_position),
            "steps": steps,
        }
    )

    config = get_config()
    result = run_command("interact_play_mode", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Runtime UI drag dispatched")


@editor.command("scroll-ui")
@click.argument("target", required=False)
@click.option(
    "--ui-system",
    type=click.Choice(["ugui", "ui_toolkit"]),
    default="ugui",
    show_default=True,
)
@click.option(
    "--position",
    nargs=2,
    type=click.FloatRange(0.0, 1.0),
    default=None,
    metavar="X Y",
    help="Normalized top-left-origin pointer coordinates.",
)
@click.option(
    "--delta",
    "scroll_delta",
    nargs=2,
    type=click.FloatRange(-100.0, 100.0),
    required=True,
    metavar="X Y",
    help="Unity scroll units; positive Y scrolls up.",
)
@click.option(
    "--search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
    help="Target lookup mode; inferred when omitted.",
)
@click.option("--document", default=None, help="UIDocument GameObject for UI Toolkit.")
@click.option(
    "--document-search-method",
    type=click.Choice(["by_id", "by_name", "by_path"]),
    default=None,
)
@click.option("--element-name", default=None, help="Exact VisualElement name.")
@click.option("--element-class", default=None, help="Required USS class.")
@click.option("--element-type", default=None, help="Exact VisualElement type.")
@click.option("--element-index", type=click.IntRange(0, 1023), default=None)
@handle_unity_errors
def scroll_ui(
    target: Optional[str],
    ui_system: str,
    position: Optional[tuple[float, float]],
    scroll_delta: tuple[float, float],
    search_method: Optional[str],
    document: Optional[str],
    document_search_method: Optional[str],
    element_name: Optional[str],
    element_class: Optional[str],
    element_type: Optional[str],
    element_index: Optional[int],
):
    """Scroll runtime uGUI or UI Toolkit."""
    if scroll_delta == (0.0, 0.0):
        raise click.UsageError("--delta cannot be 0 0.")

    params = _runtime_ui_address_params(
        action="scroll_ui",
        ui_system=ui_system,
        target=target,
        position=position,
        search_method=search_method,
        document=document,
        document_search_method=document_search_method,
        element_name=element_name,
        element_class=element_class,
        element_type=element_type,
        element_index=element_index,
    )
    params["scroll_delta"] = list(scroll_delta)

    config = get_config()
    result = run_command("interact_play_mode", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Runtime UI scroll dispatched")


@editor.command("console")
@click.option(
    "--type", "-t",
    "log_types",
    multiple=True,
    type=click.Choice(["error", "warning", "log", "all"]),
    default=["error", "warning", "log"],
    help="Message types to retrieve."
)
@click.option(
    "--count", "-n",
    default=10,
    type=int,
    help="Number of messages to retrieve."
)
@click.option(
    "--filter", "-f",
    "filter_text",
    default=None,
    help="Filter messages containing this text."
)
@click.option(
    "--stacktrace", "-s",
    is_flag=True,
    help="Include stack traces."
)
@click.option(
    "--clear",
    is_flag=True,
    help="Clear the console instead of reading."
)
@handle_unity_errors
def console(log_types: tuple, count: int, filter_text: Optional[str], stacktrace: bool, clear: bool):
    """Read or clear the Unity console.

    \b
    Examples:
        unity-mcp editor console
        unity-mcp editor console --type error --count 20
        unity-mcp editor console --filter "NullReference" --stacktrace
        unity-mcp editor console --clear
    """
    config = get_config()

    if clear:
        result = run_command("read_console", {"action": "clear"}, config)
        click.echo(format_output(result, config.format))
        if result.get("success"):
            print_success("Console cleared")
        return

    params: dict[str, Any] = {
        "action": "get",
        "types": list(log_types),
        "count": count,
        "include_stacktrace": stacktrace,
    }

    if filter_text:
        params["filter_text"] = filter_text

    result = run_command("read_console", params, config)
    click.echo(format_output(result, config.format))


@editor.command("add-tag")
@click.argument("tag_name")
@handle_unity_errors
def add_tag(tag_name: str):
    """Add a new tag.

    \b
    Examples:
        unity-mcp editor add-tag "Enemy"
        unity-mcp editor add-tag "Collectible"
    """
    config = get_config()
    result = run_command(
        "manage_editor", {"action": "add_tag", "tagName": tag_name}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Added tag: {tag_name}")


@editor.command("remove-tag")
@click.argument("tag_name")
@handle_unity_errors
def remove_tag(tag_name: str):
    """Remove a tag.

    \b
    Examples:
        unity-mcp editor remove-tag "OldTag"
    """
    config = get_config()
    result = run_command(
        "manage_editor", {"action": "remove_tag", "tagName": tag_name}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Removed tag: {tag_name}")


@editor.command("add-layer")
@click.argument("layer_name")
@handle_unity_errors
def add_layer(layer_name: str):
    """Add a new layer.

    \b
    Examples:
        unity-mcp editor add-layer "Interactable"
    """
    config = get_config()
    result = run_command(
        "manage_editor", {"action": "add_layer", "layerName": layer_name}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Added layer: {layer_name}")


@editor.command("remove-layer")
@click.argument("layer_name")
@handle_unity_errors
def remove_layer(layer_name: str):
    """Remove a layer.

    \b
    Examples:
        unity-mcp editor remove-layer "OldLayer"
    """
    config = get_config()
    result = run_command(
        "manage_editor", {"action": "remove_layer", "layerName": layer_name}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Removed layer: {layer_name}")


@editor.command("tool")
@click.argument("tool_name")
@handle_unity_errors
def set_tool(tool_name: str):
    """Set the active editor tool.

    \b
    Examples:
        unity-mcp editor tool "Move"
        unity-mcp editor tool "Rotate"
        unity-mcp editor tool "Scale"
    """
    config = get_config()
    result = run_command(
        "manage_editor", {"action": "set_active_tool", "toolName": tool_name}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Set active tool: {tool_name}")


@editor.command("deploy")
@handle_unity_errors
def deploy():
    """Deploy MCPForUnity package from configured source.

    Copies the configured MCPForUnity source folder into the project's
    installed package location. The source path must be set in the
    MCP for Unity Advanced Settings first. Triggers recompilation.

    \b
    Examples:
        unity-mcp editor deploy
    """
    config = get_config()
    result = run_command("manage_editor", {"action": "deploy_package"}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Package deployed")


@editor.command("restore")
@handle_unity_errors
def restore():
    """Restore MCPForUnity package from last backup.

    Reverts the last deployment by restoring from backup.

    \b
    Examples:
        unity-mcp editor restore
    """
    config = get_config()
    result = run_command("manage_editor", {"action": "restore_package"}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Package restored from backup")


@editor.command("undo")
@handle_unity_errors
def undo():
    """Undo the last editor action.

    \b
    Examples:
        unity-mcp editor undo
    """
    config = get_config()
    result = run_command("manage_editor", {"action": "undo"}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Undo performed")


@editor.command("redo")
@handle_unity_errors
def redo():
    """Redo the last undone action.

    \b
    Examples:
        unity-mcp editor redo
    """
    config = get_config()
    result = run_command("manage_editor", {"action": "redo"}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Redo performed")


@editor.command("menu")
@click.argument("menu_path")
@handle_unity_errors
def execute_menu(menu_path: str):
    """Execute a menu item.

    \b
    Examples:
        unity-mcp editor menu "File/Save"
        unity-mcp editor menu "Edit/Undo"
        unity-mcp editor menu "GameObject/Create Empty"
    """
    config = get_config()
    result = run_command("execute_menu_item", {"menu_path": menu_path}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Executed: {menu_path}")


@editor.command("tests")
@click.option(
    "--mode", "-m",
    type=click.Choice(["EditMode", "PlayMode"]),
    default="EditMode",
    help="Test mode to run."
)
@click.option(
    "--async", "async_mode",
    is_flag=True,
    help="Run asynchronously and return job ID for polling."
)
@click.option(
    "--wait", "-w",
    type=int,
    default=None,
    help="Wait up to N seconds for completion (default: no wait)."
)
@click.option(
    "--details",
    is_flag=True,
    help="Include detailed results for all tests."
)
@click.option(
    "--failed-only",
    is_flag=True,
    help="Include details for failed/skipped tests only."
)
@handle_unity_errors
def run_tests(mode: str, async_mode: bool, wait: Optional[int], details: bool, failed_only: bool):
    """Run Unity tests.

    \b
    Examples:
        unity-mcp editor tests
        unity-mcp editor tests --mode PlayMode
        unity-mcp editor tests --async
        unity-mcp editor tests --wait 60 --failed-only
    """
    config = get_config()

    params: dict[str, Any] = {"mode": mode}
    if wait is not None:
        params["wait_timeout"] = wait
    if details:
        params["include_details"] = True
    if failed_only:
        params["include_failed_tests"] = True

    result = run_command("run_tests", params, config)

    # For async mode, just show job ID
    if async_mode and result.get("success"):
        job_id = result.get("data", {}).get("job_id")
        if job_id:
            click.echo(f"Test job started: {job_id}")
            print_info("Poll with: unity-mcp editor poll-test " + job_id)
            return

    click.echo(format_output(result, config.format))


@editor.command("poll-test")
@click.argument("job_id")
@click.option(
    "--wait", "-w",
    type=int,
    default=30,
    help="Wait up to N seconds for completion (default: 30)."
)
@click.option(
    "--details",
    is_flag=True,
    help="Include detailed results for all tests."
)
@click.option(
    "--failed-only",
    is_flag=True,
    help="Include details for failed/skipped tests only."
)
@handle_unity_errors
def poll_test(job_id: str, wait: int, details: bool, failed_only: bool):
    """Poll an async test job for status/results.

    \b
    Examples:
        unity-mcp editor poll-test abc123
        unity-mcp editor poll-test abc123 --wait 60
        unity-mcp editor poll-test abc123 --failed-only
    """
    config = get_config()

    params: dict[str, Any] = {"job_id": job_id}
    if wait:
        params["wait_timeout"] = wait
    if details:
        params["include_details"] = True
    if failed_only:
        params["include_failed_tests"] = True

    result = run_command("get_test_job", params, config)
    click.echo(format_output(result, config.format))

    if isinstance(result, dict) and result.get("success"):
        data = result.get("data", {})
        status = data.get("status", "unknown")
        if status == "succeeded":
            print_success("Tests completed successfully")
        elif status == "failed":
            summary = data.get("result", {}).get("summary", {})
            failed = summary.get("failed", 0)
            print_error(f"Tests failed: {failed} failures")
        elif status == "running":
            progress = data.get("progress", {})
            completed = progress.get("completed", 0)
            total = progress.get("total", 0)
            print_info(f"Tests running: {completed}/{total}")


@editor.command("refresh")
@click.option(
    "--mode",
    type=click.Choice(["if_dirty", "force"]),
    default="if_dirty",
    help="Refresh mode."
)
@click.option(
    "--scope",
    type=click.Choice(["assets", "scripts", "all"]),
    default="all",
    help="What to refresh."
)
@click.option(
    "--compile",
    is_flag=True,
    help="Request script compilation."
)
@click.option(
    "--no-wait",
    is_flag=True,
    help="Don't wait for refresh to complete."
)
@handle_unity_errors
def refresh(mode: str, scope: str, compile: bool, no_wait: bool):
    """Force Unity to refresh assets/scripts.

    \b
    Examples:
        unity-mcp editor refresh
        unity-mcp editor refresh --mode force
        unity-mcp editor refresh --compile
        unity-mcp editor refresh --scope scripts --compile
    """
    config = get_config()

    params: dict[str, Any] = {
        "mode": mode,
        "scope": scope,
        "wait_for_ready": not no_wait,
    }
    if compile:
        params["compile"] = "request"

    click.echo("Refreshing Unity...")
    result = run_command("refresh_unity", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Unity refreshed")


@editor.command("custom-tool")
@click.argument("tool_name")
@click.option(
    "--params", "-p",
    default="{}",
    help="Tool parameters as JSON."
)
@handle_unity_errors
def custom_tool(tool_name: str, params: str):
    """Execute a custom Unity tool.

    Custom tools are registered by Unity projects via the MCP plugin.

    \b
    Examples:
        unity-mcp editor custom-tool "MyCustomTool"
        unity-mcp editor custom-tool "BuildPipeline" --params '{"target": "Android"}'
    """
    config = get_config()

    params_dict = parse_json_dict_or_exit(params, "params")

    result = run_command("execute_custom_tool", {
        "tool_name": tool_name,
        "parameters": params_dict,
    }, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Executed custom tool: {tool_name}")
    else:
        message = (result.get("message") or result.get("error") or "").lower()
        if "not found" in message and "tool" in message:
            try:
                tools_result = run_list_custom_tools(config)
                tools = tools_result.get("tools")
                if tools is None:
                    data = tools_result.get("data", {})
                    tools = data.get("tools") if isinstance(data, dict) else None
                names = [
                    t.get("name") for t in tools if isinstance(t, dict) and t.get("name")
                ] if isinstance(tools, list) else []
                matches = suggest_matches(tool_name, names)
                suggestion = format_suggestions(matches)
                if suggestion:
                    print_info(suggestion)
                    print_info(f'Example: unity-mcp editor custom-tool "{matches[0]}"')
            except UnityConnectionError:
                pass
