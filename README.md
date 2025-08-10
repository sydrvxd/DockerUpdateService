# DockerUpdateService (C# 12 • .NET 8 • DI)

A small, production-ready background service that updates local Docker containers and Portainer stacks on a schedule.  
Rewritten into a modern dependency-injection architecture.

## Features

- ✅ Generic Host + DI + structured logging
- ✅ Image updates for individual containers (with rollback if health fails)
- ✅ Portainer stack re-deploy if any referenced image has a newer ID
- ✅ Prunes old backup images (default retention: 5 days)
- ✅ Config via environment variables
- ✅ Dockerfile and sample docker-compose

## How it works

The worker runs an update cycle:
1. Prune outdated backups/unused tags (keeps repos that are unused entirely).
2. If Portainer is configured, scan stacks, pull referenced images, redeploy when newer, and ignore containers from those stacks in single-container checks.
3. Check non-stack containers, pull and recreate with the newer image; rollback if the new container becomes unhealthy.

## Configuration (env vars)

| Variable | Default | Description |
|---|---|---|
| `UPDATE_MODE` | `INTERVAL` | `INTERVAL`, `DAILY`, `WEEKLY`, or `MONTHLY`. |
| `UPDATE_INTERVAL` | `10m` | e.g. `30s`, `10m`, `2h`. Used only in `INTERVAL`. |
| `UPDATE_TIME` | `03:00` | Time of day (`HH:mm`) for `DAILY`, `WEEKLY`, `MONTHLY`. |
| `UPDATE_DAY` | `1` | For `WEEKLY` use day names (`Monday`…), for `MONTHLY` use `1..28`. |
| `EXCLUDE_IMAGES` | *(empty)* | Comma separated substrings for images/containers to skip. |
| `BACKUP_RETENTION_DAYS` | `5` | How long to keep backup images created before updates. |
| `CONTAINER_CHECK_SECONDS` | `10` | Time to wait for container health before rollback. |
| `PORTAINER_URL` | *(disabled)* | Base URL of your Portainer (e.g. `https://host:9443`). |
| `PORTAINER_API_KEY` | *(disabled)* | API key. If URL or key is missing, Portainer is disabled. |
| `PORTAINER_INSECURE` | `true` | Accept self-signed certs from Portainer. Set to `false` for valid certs. |
| `DOCKER_HOST` | auto | Override Docker endpoint. Otherwise uses `unix:///var/run/docker.sock` (Linux) or `npipe://./pipe/docker_engine` (Windows). |

## Build & Run (Docker)

```bash
docker build -t docker-update-service .
docker run -d --name docker-update-service \
  -e UPDATE_MODE=DAILY -e UPDATE_TIME=03:00 \
  -e PORTAINER_URL=https://portainer.local:9443 \
  -e PORTAINER_API_KEY=XXXX \
  -v /var/run/docker.sock:/var/run/docker.sock \
  --restart unless-stopped \
  docker-update-service
```

Or use `docker-compose.yml` in this repo.

## Notes

- **Rollback**: When updating a container, the current image is tagged as `backup-YYYYMMDDHHMMSS`. If the new container stops or exits with non-zero code within `{CONTAINER_CHECK_SECONDS}` seconds, the service rolls back to the backup tag.
- **Stack containers**: After (re)deploying a stack, all containers with label `com.docker.compose.project=<stack>` are added to an ignore-list so they won't be touched by the single-container updater in the same run.
- **YAML parsing**: The service performs a lightweight parse of `image:` lines in Compose YAML to avoid pulling a full YAML parser dependency.

## Develop locally

- Requires .NET 8 SDK.
- `dotnet build` or `dotnet run` in `src/DockerUpdateService`.
- On Windows, ensure access to the Docker named pipe; on Linux, mount `/var/run/docker.sock`.

---  
© 2025 MIT
