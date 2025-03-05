# DockerUpdateService

**DockerUpdateService** is a C# (.NET 8) service designed to automatically monitor and update your running Docker containers when a newer image is available. It supports backup creation, automatic rollback on failure, flexible scheduling (interval, daily, weekly, or monthly), and an exclude list to prevent certain containers from being updated.

---

## Features

- **Automatic Updates:**  
  Periodically scans your Docker containers, pulls the corresponding images, and checks if a newer version is available. If so, the service will stop, remove, and recreate the container using the updated image.

- **Backups & Rollback:**  
  Before updating a container, the service creates a backup tag of the old image. If the updated container fails to start or exits with an error within a configurable time frame, the service automatically rolls back to the backup image and excludes the container from future updates.

- **Flexible Scheduling:**  
  Configure the update frequency using environment variables. Choose between fixed intervals (e.g., every 10 minutes) or specific times:
  - **INTERVAL:** Run every X minutes/hours (e.g., `10m`).
  - **DAILY:** Run once a day at a specified time (e.g., `03:00`).
  - **WEEKLY:** Run on a specific day at a specified time (e.g., every Sunday at `03:00`).
  - **MONTHLY:** Run on a specific day of the month at a specified time (e.g., on the 1st at `03:00`).

- **Exclude List:**  
  Prevent specific containers from being updated by specifying their image names via an environment variable.

- **Automatic Cleanup:**  
  Automatically removes backup images that are older than 30 days.

---

## Getting Started

### Prerequisites

- **Docker:**  
  Ensure Docker is installed and running on your host.
  
- **.NET 8 SDK (optional):**  
  If you want to build the service from source.

---

## Installation

### Building from Source

1. **Clone the repository:**

   ```bash
   git clone https://github.com/yourusername/DockerUpdateService.git
   cd DockerUpdateService
   ```

2. **Restore NuGet packages and build:**

   ```bash
   dotnet restore
   dotnet publish -c Release -o publish
   ```

3. **Run locally:**

   ```bash
   dotnet run
   ```

### Using the Docker Image

A pre-built Docker image is available on Docker Hub:

```bash
docker pull yourusername/dockerupdateservice:latest
```

---

## Configuration

### Environment Variables

The service behavior can be customized via the following environment variables:

- **EXCLUDE_IMAGES**  
  Comma-separated list of container images that should **not** be monitored/updated.  
  _Example:_  
  ```bash
  EXCLUDE_IMAGES="openhab/openhab, redis"
  ```

- **UPDATE_MODE**  
  Determines the scheduling mode for update checks. Possible values:  
  - `INTERVAL` – Runs at a fixed interval.  
  - `DAILY` – Runs once per day at a specified time.  
  - `WEEKLY` – Runs once per week on a specified day/time.  
  - `MONTHLY` – Runs once per month on a specified day/time.  
  _Default:_ `INTERVAL`

- **UPDATE_INTERVAL**  
  Used only if `UPDATE_MODE=INTERVAL`. Specifies the time span between checks.  
  _Example:_  
  ```bash
  UPDATE_INTERVAL="10m"
  ```
  Valid formats include seconds (`s`), minutes (`m`), hours (`h`), or days (`d`).

- **UPDATE_TIME**  
  Used for `DAILY`, `WEEKLY`, or `MONTHLY` modes. Specifies the time (in `HH:mm` 24-hour format) at which the update should run.  
  _Example:_  
  ```bash
  UPDATE_TIME="03:00"
  ```

- **UPDATE_DAY**  
  Used for `WEEKLY` (e.g., `SUNDAY`) or `MONTHLY` (e.g., `1` for the 1st day of the month) modes.  
  _Example:_  
  ```bash
  UPDATE_DAY="SUNDAY"
  ```

- **DOCKER_HOST**  
  (Optional) Overrides the Docker API endpoint. Generally, this is not needed if you're mounting the Docker socket.

---

## Docker Compose

To run DockerUpdateService with Docker Compose, use a configuration similar to the following:

### For Linux

```yaml
version: '3.8'
services:
  docker-updater:
    image: yourusername/dockerupdateservice:latest
    container_name: docker-updater
    user: "root"
    privileged: true  # Use only if necessary
    environment:
      EXCLUDE_IMAGES: "openhab/openhab, redis"
      UPDATE_MODE: "INTERVAL"         # or DAILY, WEEKLY, MONTHLY
      UPDATE_INTERVAL: "10m"            # used with INTERVAL mode
      # For DAILY/WEEKLY/MONTHLY:
      # UPDATE_TIME: "03:00"
      # UPDATE_DAY: "SUNDAY"            # for WEEKLY or "1" for MONTHLY
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    restart: unless-stopped
```

### For Windows

```yaml
version: '3.8'
services:
  docker-updater:
    image: yourusername/dockerupdateservice:latest
    container_name: docker-updater
    user: "root"
    privileged: true  # Use only if necessary
    environment:
      EXCLUDE_IMAGES: "openhab/openhab, redis"
      UPDATE_MODE: "INTERVAL"
      UPDATE_INTERVAL: "10m"
      # For DAILY/WEEKLY/MONTHLY:
      # UPDATE_TIME: "03:00"
      # UPDATE_DAY: "SUNDAY"
    volumes:
      - \\.\pipe\docker_engine:\\.\pipe\docker_engine
    restart: unless-stopped
```

---

## Dockerfile Compatibility

The Dockerfile is designed to be compatible with both Linux and Windows containers by using a build argument to select the base image:

```dockerfile
# syntax=docker/dockerfile:1
ARG BASE_IMAGE=mcr.microsoft.com/dotnet/runtime:8.0
FROM ${BASE_IMAGE} AS base
WORKDIR /app
COPY publish/ .
ENTRYPOINT ["DockerUpdateService"]
```

To build for Linux:

```bash
docker build -t dockerupdateservice:linux .
```

To build for Windows:

```bash
docker build --build-arg BASE_IMAGE=mcr.microsoft.com/dotnet/runtime:8.0-nanoserver -t dockerupdateservice:windows .
```

---

## Logging & Troubleshooting

- **Logs:**  
  View logs with:
  ```bash
  docker logs -f docker-updater
  ```

- **Common Issues:**  
  - **Connection failed / Permission denied:**  
    Ensure the Docker socket (or named pipe) is correctly mounted and that the container is running as root or has proper permissions.
  - **Update or rollback failures:**  
    Review logs for detailed error messages and adjust configuration or scheduling parameters if necessary.

---

## Contributing

Contributions, bug fixes, and improvements are welcome! Please open an issue or submit a pull request.

---

*Happy Updating!*
