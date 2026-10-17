#!/usr/bin/env bash
set -euo pipefail

echo "=== Agent360 Production Environment Provisioning ==="
echo "Target Host: 2nd server"
echo "Surfaces: frontend (agent360.antares.vn), backend (api-agent360.antares.vn), community (community-agent360.antares.vn)"

TARGET_DIR="/opt/agent360"
mkdir -p "${TARGET_DIR}/deploy"

echo "Copying deployment descriptors..."
cp projects/agent360/backend/deploy/docker-compose.prod.yml "${TARGET_DIR}/deploy/"
cp projects/agent360/backend/deploy/docker-compose.community.yml "${TARGET_DIR}/deploy/"

echo "Generating sanitized production env..."
cat << 'ENVFILE' > "${TARGET_DIR}/deploy/.env"
# Agent360 Production Environment Secrets
DISCOURSE_ADMIN_EMAIL=admin@antares.vn
DISCOURSE_DB_PASSWORD=$(openssl rand -hex 16)
DISCOURSE_REDIS_PASSWORD=$(openssl rand -hex 16)
SMTP_HOST=mail.antares.vn
SMTP_PORT=587
SMTP_USER=community@antares.vn
SMTP_PASSWORD=changeme_in_infisical
# DATABASE_URL and QwenPaw instance credentials are NOT written here: the backend reads
# /opt/agent360/deploy/backend.env, rendered from Infisical core/prod:/agent360-backend/app/backend
# by deploy/agentteams-a2a/scripts/render_secrets.py backend --ssh root@<agent360-node>.
QWENPAW_BASE_URL=http://qwenpaw:8000
ENVFILE

chmod 600 "${TARGET_DIR}/deploy/.env"
echo "Production environment prepared successfully."
