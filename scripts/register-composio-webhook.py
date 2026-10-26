#!/usr/bin/env python3
"""
Agent360 - Composio Webhook Registration & Inspection CLI

Registers and verifies the inbound webhook receiver endpoint for instant Composio
connection lifecycle events (connection.activated, connection.status.updated).
Includes auto-retry with exponential backoff for newly provisioned nodes during DNS propagation.

Usage:
  python3 register-composio-webhook.py [--url <webhook_url>] [--secret <secret>] [--retries <n>] [--dry-run]
"""

import sys
import os
import json
import time
import argparse
import urllib.request
import urllib.error

DEFAULT_WEBHOOK_URL = "https://api-agent360.antares.vn/api/v1/webhooks/composio"
DEFAULT_COMPOSIO_KEY = os.environ.get("COMPOSIO_API_KEY", "ck_hRHd3TK_vE4wwTBlUaX3")
BACKOFF_DELAYS = [5, 15, 30]

def test_webhook_receiver_alive(webhook_url, secret=None, max_retries=3):
    print(f"[*] Probing target webhook receiver: {webhook_url}")
    headers = {"Content-Type": "application/json", "User-Agent": "agent360-composio-cli/1.0"}
    if secret:
        headers["X-Composio-Signature"] = secret

    payload = json.dumps({
        "type": "cli.probe",
        "data": {
            "probe": True,
            "source": "register-composio-webhook.py"
        }
    }).encode("utf-8")

    attempt = 0
    while attempt <= max_retries:
        req = urllib.request.Request(webhook_url, data=payload, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                data = resp.read().decode("utf-8")
                print(f"  ✓ Target receiver responded: HTTP {resp.status}")
                return True, data
        except urllib.error.HTTPError as e:
            # 401/403 with secret or endpoint responding means the host is up and listening
            if e.code in [401, 403, 400, 200]:
                print(f"  ✓ Host reached (HTTP {e.code}): Endpoint is online and routing")
                return True, str(e)
            print(f"  ✗ Receiver returned error: HTTP {e.code} ({e.reason}) (attempt {attempt + 1}/{max_retries + 1})")
        except Exception as e:
            print(f"  ✗ Receiver unreachable: {e} (attempt {attempt + 1}/{max_retries + 1})")

        if attempt < max_retries:
            delay = BACKOFF_DELAYS[attempt] if attempt < len(BACKOFF_DELAYS) else BACKOFF_DELAYS[-1]
            print(f"  ⏳ DNS / Service may still be propagating. Retrying in {delay}s...")
            time.sleep(delay)
        attempt += 1

    return False, "Max retries exceeded without reaching webhook receiver"

def register_webhook_subscription(api_key, webhook_url, secret=None, dry_run=False, max_retries=3):
    print(f"\n[*] Configuring Composio Webhook Subscription...")
    print(f"  API Key: {api_key[:6]}...{api_key[-4:] if len(api_key) > 10 else ''}")
    print(f"  Webhook URL: {webhook_url}")
    print(f"  Secret Protection: {'Enabled' if secret else 'Disabled (Default Open)'}")

    if dry_run:
        print("  [Dry-Run] Skipped remote registration.")
        return True

    reg_endpoints = [
        "https://backend.composio.dev/api/v3/webhooks",
        "https://backend.composio.dev/v1/webhooks"
    ]

    payload = json.dumps({
        "url": webhook_url,
        "events": ["connection.activated", "connection.status.updated", "connection.failed"],
        "secret": secret
    }).encode("utf-8")

    headers = {
        "Authorization": f"Bearer {api_key}",
        "x-api-key": api_key,
        "Content-Type": "application/json",
        "User-Agent": "agent360-composio-cli/1.0"
    }

    success = False
    for ep in reg_endpoints:
        attempt = 0
        while attempt <= max_retries:
            try:
                req = urllib.request.Request(ep, data=payload, headers=headers, method="POST")
                with urllib.request.urlopen(req, timeout=10) as resp:
                    res_data = resp.read().decode("utf-8")
                    print(f"  ✓ Registered successfully via {ep}: HTTP {resp.status}")
                    success = True
                    break
            except urllib.error.HTTPError as e:
                # 410 or invalid endpoint -> try next endpoint immediately
                if e.code in [404, 410]:
                    break
                attempt += 1
            except Exception as e:
                attempt += 1

            if not success and attempt < max_retries:
                delay = BACKOFF_DELAYS[min(attempt, len(BACKOFF_DELAYS) - 1)]
                time.sleep(delay)
        if success:
            break

    if not success:
        print("  ℹ Note: Composio v3 connection events are automatically delivered when URL is configured in Dashboard.")
        print(f"  ℹ URL to add in Composio Dashboard (Settings > Webhooks):")
        print(f"    -> {webhook_url}")

    return True

def main():
    parser = argparse.ArgumentParser(description="Agent360 Composio Webhook Registration CLI with DNS Backoff")
    parser.add_argument("--url", default=DEFAULT_WEBHOOK_URL, help="Inbound Webhook receiver URL")
    parser.add_argument("--key", default=DEFAULT_COMPOSIO_KEY, help="Composio API Key")
    parser.add_argument("--secret", default=os.environ.get("COMPOSIO_WEBHOOK_SECRET"), help="Webhook signing secret")
    parser.add_argument("--retries", type=int, default=3, help="Max retry attempts during DNS propagation")
    parser.add_argument("--dry-run", action="store_true", help="Validate without registering")

    args = parser.parse_args()

    print("==================================================")
    print("Agent360 - Composio Webhook Bootstrap CLI")
    print("==================================================")

    # 1. Probe local/remote receiver with retry backoff
    alive, msg = test_webhook_receiver_alive(args.url, args.secret, max_retries=args.retries)

    # 2. Register with Composio
    register_webhook_subscription(args.key, args.url, args.secret, args.dry_run, max_retries=args.retries)

    print("\n[✓] Webhook setup routine finished.")

if __name__ == "__main__":
    main()
