"""Probably Stolen structured gameplay bridge client."""
from .client import BridgeError, Client, OutcomeUnknown, discover_bridge_dir

__all__ = ["BridgeError", "Client", "OutcomeUnknown", "discover_bridge_dir"]
