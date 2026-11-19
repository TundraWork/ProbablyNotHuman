"""Observe the live game and optionally execute a discovered action once."""
import argparse
import json
from probably_not_human import Client

parser = argparse.ArgumentParser()
parser.add_argument("--bridge-dir")
parser.add_argument("--action", help="An action name returned by describe")
parser.add_argument("--arguments", default="{}", help="JSON arguments matching that action's schema")
args = parser.parse_args()
client = Client(args.bridge_dir)
print(json.dumps(client.observe(), indent=2))
available = client.describe()
print(json.dumps(available, indent=2))
if args.action:
    # No vendor SDK, screenshot, or retry loop. Consult describe's schemas first.
    print(json.dumps(client.act(args.action, json.loads(args.arguments)), indent=2))
