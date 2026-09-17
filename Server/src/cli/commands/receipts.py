"""Administrative receipt-ledger diagnostics and cleanup."""

import click

from cli.utils.config import get_config
from cli.utils.connection import handle_unity_errors, run_receipt_ledger_admin
from cli.utils.output import format_output, print_success, print_warning


@click.group()
def receipts():
    """Diagnose or safely clean the Unity command receipt ledger."""
    pass


@receipts.command("status")
@handle_unity_errors
def status():
    """Show receipt counts by lifecycle state without changing the ledger."""
    config = get_config()
    result = run_receipt_ledger_admin(action="diagnose", config=config)
    click.echo(format_output(result, config.format))


@receipts.command("cleanup")
@click.option(
    "--all-terminal",
    is_flag=True,
    help="Delete known terminal receipts regardless of age.",
)
@click.option(
    "--confirm-outcome-unknown",
    is_flag=True,
    help=(
        "Also delete selected outcome_unknown receipts. Their side effects may "
        "have occurred, so this requires a separate explicit confirmation."
    ),
)
@handle_unity_errors
def cleanup(all_terminal: bool, confirm_outcome_unknown: bool):
    """Delete expired terminal receipts; active receipts are always preserved."""
    config = get_config()
    scope = "all_terminal" if all_terminal else "expired_terminal"
    result = run_receipt_ledger_admin(
        action="cleanup",
        scope=scope,
        confirm_outcome_unknown=confirm_outcome_unknown,
        config=config,
    )
    click.echo(format_output(result, config.format))

    if result.get("success"):
        print_success(f"Removed {result.get('removed_total', 0)} receipts")
        preserved = result.get("preserved_outcome_unknown", 0)
        if preserved:
            print_warning(
                f"Preserved {preserved} selected outcome_unknown receipts; "
                "repeat with --confirm-outcome-unknown only after recovery review."
            )
