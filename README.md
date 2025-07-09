
# DockerUpdateService

**DockerUpdateService** is a lightweight, container‑friendly daemon written in **C# 12 / .NET 8** that automatically keeps your self‑hosted Docker workloads up‑to‑date.

It works in two complementary modes:

| Mode | What it does | Typical use‑case |
|------|--------------|------------------|
| **Stack‑level** | • Pulls the images referenced in every Portainer stack.<br>• If any image was updated, triggers a *Portainer* **stack redeploy**.<br>• Adds the stack’s containers to an *ignore* list so they are **not** processed again by the container‑level logic. | When you manage services with **docker‑compose** files through *Portainer*. |
| **Container‑level** | • Iterates over the remaining containers, pulls the tag they were started with, and if the image ID changed:<br>&nbsp;&nbsp;– Stops the running container.<br>&nbsp;&nbsp;– Creates a *backup tag* (`:rollback‑yyyyMMddHHmmss`).<br>&nbsp;&nbsp;– Starts a **fresh container** with the new image and the same configuration.<br>&nbsp;&nbsp;– Waits until the container reports a healthy state, otherwise **rolls back**. | Stand‑alone containers started with `docker run` / `docker create`. |

Other goodies include:

* **Pruning** of unused images **and** automatic deletion of old backup tags after the configurable retention period.
* **Flexible scheduling** – run on a fixed interval *or* at a specific time daily / weekly / monthly.
* **Minimal configuration** – a single `appsettings.json` (or environment variables / secrets).
* First‑class **Portainer** integration (API key authentication).

---

## Quick start

### 1. Build the container image

```bash
# in the repository root
docker build -t docker-update-service .
```

### 2. Provide configuration

Create a volume‑mapped `appsettings.json` (see full schema below) or use environment variables:

```yaml
volumes:
  - ./appsettings.json:/app/appsettings.json:ro
```

### 3. Run

```bash
docker run   --name docker-update-service   -v /var/run/docker.sock:/var/run/docker.sock   -v ./appsettings.json:/app/appsettings.json:ro   --restart unless-stopped   docker-update-service
```

The container needs access to the **Docker socket** (or the Windows named pipe) to manage images and containers.

---

## Configuration reference (`appsettings.json`)

```jsonc
{
  "Update": {
    "Portainer": {
      "Url": "https://portainer.example.com/api/",
      "ApiKey": "<YOUR-API-KEY>"
    },

    // Images that must never be updated (substring match).
    "ExcludeImages": [ "mongo:3", "mycorp/legacy" ],

    // How long to keep backup tags (ISO 8601 or "d.hh:mm:ss").
    "BackupRetention": "5.00:00:00"
  },

  "Schedule": {
    // "Interval" | "Daily" | "Weekly" | "Monthly"
    "Mode": "Daily",

    // Only used in Interval mode (hh:mm:ss).
    "Interval": "00:30:00",

    // Run at 03:00 local time (Daily/Weekly/Monthly)
    "TimeOfDay": "03:00:00",

    // Run every Monday at 03:00 (Weekly)
    "DayOfWeek": "Monday"
  }
}
```

All settings can also be supplied via environment variables using the `__` (double underscore) separator, e.g.:

```bash
# same as Update:Portainer:ApiKey in appsettings.json
-e "Update__Portainer__ApiKey=XYZ..."
```

---

## Building & running from source

```bash
dotnet build -c Release
dotnet publish -c Release -o ./publish
cd publish
DOTNET_ENVIRONMENT=Production dotnet DockerUpdateService.dll
```

---

## Internals & design notes

* **BackgroundService** `UpdateWorker` coordinates one cycle: `Pruner ➜ StackUpdater ➜ ContainerUpdater`.
* The Docker API is accessed through [**Docker.DotNet**](https://github.com/christopherrej/Docker.DotNet).  
  The client automatically connects to `unix:///var/run/docker.sock` on Linux and `npipe://./pipe/docker_engine` on Windows.
* Portainer interaction is encapsulated in `PortainerClient` and only enabled when both **Url** *and* **ApiKey** are provided.
* All I/O is **asynchronous**; the service is entirely CPU‑bound when parsing YAML and computing diffs.
* The implementation is **nullable‑aware** and leverages new C# 12 features such as *primary constructors* and *collection expressions* for succinctness.

---

## License

Licensed under the MIT license. See `LICENSE.txt` for details.


### Portainer integration (optional)

If you want the service to redeploy **Portainer stacks**, add a `Portainer` block
inside the `Update` section **or** export the corresponding environment variables.

```jsonc
{
  "Update": {
    "Portainer": {
      "Url": "https://portainer.yourdomain.example",
      "ApiKey": "<PORTAINER_API_KEY>"
    }
  }
}
```

| Environment variable | Matches config key |
|----------------------|--------------------|
| `UPDATE__PORTAINER__URL` | `Update:Portainer:Url` |
| `UPDATE__PORTAINER__APIKEY` | `Update:Portainer:ApiKey` |

**Leave the keys empty or omit the block entirely to disable Portainer support.**

## Configuration via environment variables

Every configuration key can be supplied through **environment variables** &mdash; ideal when running inside Docker or Kubernetes.  
The rule is simple: replace each `:` level separator with a double underscore `__`.

| Section & Key | Example value | Environment variable |
|---------------|---------------|----------------------|
| `Update:ExcludeImages:0` | `hello/world` | `UPDATE__EXCLUDEIMAGES__0` |
| `Update:ExcludeImages:1` | `busybox:latest` | `UPDATE__EXCLUDEIMAGES__1` |
| `Update:BackupRetention` | `2.00:00:00` (2 days) | `UPDATE__BACKUPRETENTION` |
| `Update:Portainer:Url` | `https://portainer.mycorp.local` | `UPDATE__PORTAINER__URL` |
| `Update:Portainer:ApiKey` | `<your api key>` | `UPDATE__PORTAINER__APIKEY` |
| `Schedule:Mode` | `Weekly` | `SCHEDULE__MODE` |
| `Schedule:Interval` | `00:30:00` | `SCHEDULE__INTERVAL` |
| `Schedule:TimeOfDay` | `03:00` | `SCHEDULE__TIMEOFDAY` |
| `Schedule:DayOfWeek` | `Sunday` | `SCHEDULE__DAYOFWEEK` |

### Quick example (docker-compose)

```yaml
services:
  updater:
    image: docker-update-service:latest
    environment:
      # run every Sunday at 03:00
      SCHEDULE__MODE: Weekly
      SCHEDULE__TIMEOFDAY: "03:00"
      SCHEDULE__DAYOFWEEK: Sunday

      # keep old images for 5 days
      UPDATE__BACKUPRETENTION: 5.00:00:00

      # ignore these images completely
      UPDATE__EXCLUDEIMAGES__0: "library/nginx:alpine"
      UPDATE__EXCLUDEIMAGES__1: "some/other-image"

      # enable Portainer stack re‑deploy
      UPDATE__PORTAINER__URL: https://portainer.mycorp.local/api
      UPDATE__PORTAINER__APIKEY: "${PORTAINER_API_KEY}"
```

> **Tip:** ASP.NET’s configuration binder is case‑insensitive, so
> `schedule__mode`, `Schedule__Mode`, or `SCHEDULE__MODE` all work the same.
