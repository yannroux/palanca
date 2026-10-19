# Deploying Palanca on the HogarHero VPS

Palanca runs on the HogarHero VPS as its own Docker Compose project, `palanca`, independent of HogarHero's releases. This runbook covers the first installation, upgrades, backups, and the drills. Commands run on the VPS as root unless they say otherwise.

## How it fits

| Piece | Where |
|-------|-------|
| Compose project | `/opt/palanca`: `compose.yml`, `deploy.sh`, and `.env`, which holds the image digest |
| Data | the `palanca_data` volume, mounted at `/data` |
| Backups | `/opt/palanca/backups` on the host, mounted at `/backups`, owned by UID 1654 with mode 0700 |
| Dashboard and management API | `https://palanca.hogarhero.com`, through the Cloudflare proxy and HogarHero's Caddy |
| Evaluation API, health, metrics | private Docker networks only: `http://palanca:8080` |

The service joins three of HogarHero's networks as external networks, so HogarHero's Compose project (`hogarhero-production`) must be up before Palanca starts:

- **`hogarhero-production_edge`**: Caddy reaches the dashboard here, through the alias `palanca-dashboard`. Palanca has the fixed address `172.30.10.20`, because HogarHero's containers on this network have fixed addresses too (Caddy `.2`, `web` `.3`, `web-staging` `.4`). Caddy's address, `172.30.10.2`, is Palanca's only trusted proxy.
- **`hogarhero-production_backend`**: production's Web and Worker reach the evaluation API here.
- **`hogarhero-production_staging-backend`**: staging's Web and Worker reach it here.

## First installation

1. **Install the project directory, the backup directory, and the backup timer.** From a checkout of this repository on the VPS:

   ```sh
   sudo deploy/vps/install.sh
   ```

   The timer is enabled from now on. Its runs fail until the first deploy, because there is nothing to back up yet.

2. **Find the image digest.** Every push to `main` publishes `ghcr.io/yannroux/palanca:sha-<full commit SHA>`, and the CI run's summary shows the image reference with its digest. Or read it:

   ```sh
   docker buildx imagetools inspect ghcr.io/yannroux/palanca:sha-<commit> --format '{{json .Manifest.Digest}}'
   ```

3. **Deploy it.** The script accepts a digest only, never a tag, and waits until the container is healthy:

   ```sh
   sudo /opt/palanca/deploy.sh ghcr.io/yannroux/palanca@sha256:<digest>
   ```

4. **Publish the dashboard.** These changes belong to HogarHero's repository and reach the VPS only through its `main` branch:
   - a proxied Cloudflare DNS record for `palanca.hogarhero.com`, like the one for `staging.hogarhero.com`;
   - the site block below in `deploy/Caddyfile`.

   ```caddyfile
   palanca.hogarhero.com {
   	tls /certs/hogarhero-origin.pem /certs/hogarhero-origin.key
   	encode zstd gzip
   	header {
   		Strict-Transport-Security "max-age=31536000; includeSubDomains"
   		X-Robots-Tag "noindex, nofollow, noarchive"
   		-Server
   	}

   	# Only the dashboard, its static files, and the management API. The evaluation API, the health endpoints and
   	# /metrics stay on the private networks.
   	@palanca path / /sign-in /sign-in/options /sign-out /enroll /enroll/options /passkeys /passkeys/options /history /tokens /apps/* /api/manage/* /css/* /js/* /fonts/* /favicon*
   	handle @palanca {
   		reverse_proxy palanca-dashboard:8080 {
   			header_up X-Real-IP {client_ip}
   		}
   	}
   	handle {
   		respond 404
   	}
   }
   ```

   Palanca sends its own content security policy and frame, content type, and referrer headers, so the block adds only HSTS and `X-Robots-Tag`, and removes `Server`. `{client_ip}` is the visitor's address, which Caddy reads from `CF-Connecting-IP` because Cloudflare's ranges are its trusted proxies. The origin certificate must cover `palanca.hogarhero.com`.

5. **Create the owner account.** The command prints a link that works once, within 15 minutes. Open it on the device that holds the passkey, then add a second passkey on another device from the Passkeys page.

   ```sh
   cd /opt/palanca && sudo docker compose exec palanca dotnet Palanca.dll owner enroll <email>
   ```

   The link is a credential. Do not paste it into a ticket, a chat, or a log.

## Upgrades and rollback

Deploy the new digest with `deploy.sh`. It prints the previous digest first. To roll back, deploy that digest again.

At startup, a new image applies pending migrations, after copying the database to `/opt/palanca/backups` as `palanca-<time>-before-<migration>.db`. An older image may not run on a migrated database. If it does not, restore that copy (below) and then deploy the older image.

## Health, logs, and metrics

```sh
cd /opt/palanca
sudo docker compose ps                         # healthy, from the image's health check
sudo docker compose logs --since 1h palanca    # JSON, one object per line
curl -s http://172.30.10.20:8080/health/ready  # from the host, through the edge network
curl -s http://172.30.10.20:8080/metrics
```

The README describes the metrics.

## Backups

The `palanca-backup.timer` runs `backup` daily at 07:50 UTC, with up to 10 minutes of random delay, after HogarHero's PostgreSQL backup. It keeps 14 days of daily copies.

```sh
systemctl list-timers palanca-backup.timer
journalctl -u palanca-backup.service --since -2d
```

Run the commands by hand from `/opt/palanca`:

```sh
sudo docker compose exec palanca dotnet Palanca.dll backup
sudo docker compose exec palanca dotnet Palanca.dll backup verify /backups/palanca-<time>.db
```

`backup verify` on `/data/palanca.db` reads the live database and prints its row counts, for comparing with a copy.

### Restore

```sh
cd /opt/palanca
sudo docker compose stop palanca
sudo docker compose run --rm palanca restore /backups/palanca-<time>.db
sudo docker compose start palanca
```

`restore` checks the copy first and refuses one from a newer version. It keeps the database it replaces in the volume as `palanca-replaced-<time>.db`.

### Restore drill on a scratch host (AC-009)

1. On the VPS, take a backup, then print the live row counts right after it:

   ```sh
   cd /opt/palanca
   sudo docker compose exec palanca dotnet Palanca.dll backup
   sudo docker compose exec palanca dotnet Palanca.dll backup verify /data/palanca.db
   ```

2. Copy the backup and its `.sha256` file to the scratch host. Make them readable by UID 1654 there:

   ```sh
   mkdir drill && cp palanca-<time>.db palanca-<time>.db.sha256 drill/ && sudo chown -R 1654:1654 drill
   ```

3. Restore it into a new volume with the same image, then count what came back:

   ```sh
   image=ghcr.io/yannroux/palanca@sha256:<digest running on the VPS>
   docker run --rm --volume "$PWD/drill:/backups:ro" --volume palanca-drill:/data "$image" restore /backups/palanca-<time>.db
   docker run --rm --volume palanca-drill:/data "$image" backup verify /data/palanca.db
   ```

4. Every table's row count must match the VPS's counts from step 1, unless something changed on the VPS in between. Remove the volume afterwards: `docker volume rm palanca-drill`.

## Outage drill (AC-007)

Run it on HogarHero staging, before production depends on any flag.

1. Note a flag's value as HogarHero staging serves it.
2. Stop Palanca: `cd /opt/palanca && sudo docker compose stop palanca`.
3. For 10 minutes, staging keeps serving the noted value. Its health is Healthy, then Degraded, never Unhealthy, once the flags are more than 5 minutes old.
4. While Palanca is still down, restart staging's Web container. It starts within the client's startup wait (2 seconds by default) plus its usual startup time, and uses the code defaults.
5. Start Palanca: `sudo docker compose start palanca`. Change the flag in the dashboard. Staging serves the new value within one poll interval (30 seconds by default).
