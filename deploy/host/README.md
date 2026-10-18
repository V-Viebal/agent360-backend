# Agent360 host deploy surface (node 2nd)

Versioned copies of the files that live on the host. CI does not install them; apply with:

    scp deploy/host/agent360-restart root@2nd:/tmp/ && ssh root@2nd install -m 0755 /tmp/agent360-restart /usr/local/sbin/agent360-restart
    scp deploy/docker-compose.prod.yml    root@2nd:/opt/agent360/deploy/docker-compose.prod.yml
    scp deploy/docker-compose.staging.yml root@2nd:/opt/agent360-staging/docker-compose.yml

- `agent360-restart` is the only command the GitHub runner may run with sudo. It holds a deploy lock, recreates a backend
  only when `backend.env` changed (`--pull never`; the image is a runtime and code is bind-mounted at `/app`), fails the job
  when the recreate fails, and installs a CI-rendered nginx conf (`nginx -t` before reload, reload only on change).
- Frontends mount the whole `nginx-conf/` directory, so replacing `default.conf` atomically is visible without recreate.
- Never `docker restart`/recreate the frontends by hand: it cuts live SSE chats. Use the hook or CI.
