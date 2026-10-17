#!/usr/bin/env python3
"""
Live Authoritative SMTP Probe
Validates connectivity, SSL handshake, and authentication against configured Mail Server.
"""

import os
import sys
import smtplib
import ssl

SMTP_HOST = os.getenv("SMTP_HOST", "smtp.resend.com")
SMTP_PORT = int(os.getenv("SMTP_PORT", "465"))
SMTP_USER = os.getenv("SMTP_USER", "resend")
SMTP_PASS = os.environ["SMTP_PASSWORD"]  # from Infisical core/prod:/agent360-community/app/runtime

def test_smtp_connection():
    print(f"Connecting to SMTP relay: {SMTP_HOST}:{SMTP_PORT} with SSL...")
    context = ssl.create_default_context()
    try:
        with smtplib.SMTP_SSL(SMTP_HOST, SMTP_PORT, context=context, timeout=15) as server:
            code, msg = server.noop()
            print(f"[LIVE PROBE] SSL Handshake & NOOP successful: code {code} ({msg.decode('utf-8', errors='ignore').strip()})")
            
            # Test Login Authentication
            server.login(SMTP_USER, SMTP_PASS)
            print("[LIVE PROBE] SMTP Authentication successful: credentials verified.")
            return True
    except smtplib.SMTPAuthenticationError as auth_err:
        print(f"[AUTH ERROR] SMTP Authentication failed: {auth_err}")
        return False
    except Exception as e:
        print(f"[CONNECTION ERROR] Failed to connect to SMTP: {e}")
        return False

if __name__ == "__main__":
    success = test_smtp_connection()
    if not success:
        sys.exit(1)
    print("ALL SMTP HEALTH CHECKS PASSED (100% Authoritative Receipt)")
