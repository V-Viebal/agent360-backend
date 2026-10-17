#!/usr/bin/env bash
set -euo pipefail

echo "======================================================="
echo "   Agent360 Ecosystem Production Deployment & Mail     "
echo "======================================================="
echo "Target Host: 2nd server"
echo "Frontend:  https://agent360.antares.vn"
echo "Backend:   https://api-agent360.antares.vn"
echo "Community: https://community-agent360.antares.vn"
echo "Mail Host: smtp.resend.com:465 (SSL Verified)"
echo "======================================================="

DEPLOY_ROOT="/opt/agent360"
sudo mkdir -p "${DEPLOY_ROOT}/deploy"

echo "1. Verifying SMTP Relay connectivity..."
: "${SMTP_PASSWORD:?SMTP_PASSWORD must be exported from Infisical core/prod:/agent360-community/app/runtime}"
SMTP_PASSWORD="${SMTP_PASSWORD}" python3 -c '
import os, smtplib, ssl
context = ssl.create_default_context()
with smtplib.SMTP_SSL("smtp.resend.com", 465, context=context, timeout=10) as s:
    s.login("resend", os.environ["SMTP_PASSWORD"])
    print("SMTP Live Check: 100% OK")
'

echo "2. Copying compose descriptors..."
sudo cp projects/agent360/backend/deploy/docker-compose.prod.yml "${DEPLOY_ROOT}/deploy/docker-compose.prod.yml"
sudo cp projects/agent360/backend/deploy/docker-compose.community.yml "${DEPLOY_ROOT}/deploy/docker-compose.community.yml"

echo "3. Synchronizing production secrets..."
sudo SMTP_PASSWORD="${SMTP_PASSWORD}" bash -c "cat << ENVFILE > ${DEPLOY_ROOT}/deploy/.env
# Discourse Community & Core Configuration
DISCOURSE_ADMIN_EMAIL=admin@antares.vn
DISCOURSE_DB_PASSWORD=$(openssl rand -hex 16)
DISCOURSE_REDIS_PASSWORD=$(openssl rand -hex 16)
SMTP_HOST=smtp.resend.com
SMTP_PORT=465
SMTP_USER=resend
SMTP_PASSWORD=${SMTP_PASSWORD}
DATABASE_URL=Host=postgres;Port=5432;Database=agent360_prod;Username=agent360;Password=$(openssl rand -hex 16)
QWENPAW_BASE_URL=http://qwenpaw:8000
ENVFILE"
sudo chmod 600 "${DEPLOY_ROOT}/deploy/.env"

echo "4. Deploying Community Discourse Stack..."
sudo docker compose -f "${DEPLOY_ROOT}/deploy/docker-compose.community.yml" --env-file "${DEPLOY_ROOT}/deploy/.env" up -d

echo "5. Deploying Agent360 Core Stack (Frontend + Backend API)..."
sudo docker compose -f "${DEPLOY_ROOT}/deploy/docker-compose.prod.yml" --env-file "${DEPLOY_ROOT}/deploy/.env" up -d

echo "6. Performing Live Container Health Check..."
sudo docker compose -f "${DEPLOY_ROOT}/deploy/docker-compose.community.yml" ps
sudo docker compose -f "${DEPLOY_ROOT}/deploy/docker-compose.prod.yml" ps

echo "======================================================="
echo "   Deployment execution & Live Checks Completed!      "
echo "======================================================="
