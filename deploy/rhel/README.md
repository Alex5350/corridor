# RHEL 9 deployment notes

Corridor ships two RHEL flavored options: plain systemd units running
`dotnet` publish output, or the container image built from
`deploy/rhel/Dockerfile.ubi9` (works with podman/buildah on RHEL). Both are
documented deployment patterns, not cloud hosted; the repo actually demos
locally via `docker-compose.yml`.

Everything below assumes RHEL 9 x86_64 with an active subscription (or UBI
repositories when using the container path). All values shown are the
documented synthetic demo values.

## Assumptions

- SQL Server (or the azure-sql-edge container) is reachable from the host as
  `sql.corridor.internal:1433` in the examples; adjust to your site. Apply
  `db/sql/001_schemas.sql`, `db/sql/002_trace_procs.sql` and
  `db/sql/seed/003_seed.sql` in that order with sqlcmd before starting the
  services.
- Placeholder hostnames in the unit files (`oktasim.corridor.internal`,
  `adfssim.corridor.internal`, `legacy.corridor.internal`) map to wherever
  the peer services run; keep them consistent because the okta-sim issuer
  string must match what the other services expect.
- All demo ports are unprivileged (5200, 8000, 8080, 8090, 5173).

## Prerequisites (dnf)

Runtime only (production host):

    sudo dnf install -y aspnetcore-runtime-10.0 libicu

SDK as well (when publishing on the host itself):

    sudo dnf install -y dotnet-sdk-10.0

If your RHEL 9 minor release does not carry `aspnetcore-runtime-10.0` in
AppStream yet, verify with `dnf list aspnetcore-runtime-10.0`, or use the
Microsoft dotnet channel for RHEL 9 (packages.microsoft.com/rhel/9/prod) as
documented upstream.

## Publish and layout

    dotnet publish src/Corridor.Portal/Corridor.Portal.csproj  -c Release -o /opt/corridor/portal
    dotnet publish src/Corridor.OktaSim/Corridor.OktaSim.csproj  -c Release -o /opt/corridor/oktasim
    dotnet publish src/Corridor.Legacy/Corridor.Legacy.csproj   -c Release -o /opt/corridor/legacy

Copy the shared demo material so the services find it at the paths baked into
the unit files:

    sudo mkdir -p /opt/corridor/certs /opt/corridor/policies
    sudo cp certs/* /opt/corridor/certs/
    sudo cp policies/*.xacml.xml /opt/corridor/policies/

Service account:

    sudo useradd --system --home /opt/corridor --shell /usr/sbin/nologin corridor
    sudo chown -R corridor:corridor /opt/corridor

Secrets: the unit files carry demo values inline. For anything beyond the
demo, replace them with `EnvironmentFile=/etc/corridor/<service>.env` (root
owned, mode 600) so real connection strings never sit in unit files.

## Install the systemd units

    sudo cp deploy/rhel/corridor-*.service /etc/systemd/system/
    sudo systemctl daemon-reload
    sudo systemctl enable --now corridor-oktasim.service
    sudo systemctl enable --now corridor-legacy.service
    sudo systemctl enable --now corridor-portal.service

Check health (every service exposes anonymous `/healthz` returning
`{"status":"ok"}`):

    curl -s http://localhost:8080/healthz
    curl -s http://localhost:8000/healthz
    curl -s http://localhost:5200/healthz

Logs: `journalctl -u corridor-portal.service -f`.

## firewalld

Open the ports you expose to users. The demo minimum for the three services
above:

    sudo firewall-cmd --permanent --add-port=5200/tcp
    sudo firewall-cmd --permanent --add-port=8000/tcp
    sudo firewall-cmd --permanent --add-port=8080/tcp
    sudo firewall-cmd --reload

Add `8090/tcp` (adfs-sim) and `5173/tcp` (SPA) if you host those on the same
box.

## SELinux

When a web facing service process opens its SQL Server connection, SELinux
can block the outbound connect with an AVC denial. Allow it with the
documented boolean:

    sudo setsebool -P httpd_can_network_connect_db 1

If connections still fail, check for denials and report them before relaxing
anything else:

    sudo ausearch -m avc -ts recent

## Container alternative (Dockerfile.ubi9)

Publish each service to a staging folder, then build with podman (or docker)
from the repo root; see the header comment in `deploy/rhel/Dockerfile.ubi9`
for the exact build args:

    dotnet publish src/Corridor.Portal/Corridor.Portal.csproj -c Release -o portal-publish
    podman build -f deploy/rhel/Dockerfile.ubi9 \
      --build-arg PUBLISH_DIR=portal-publish \
      --build-arg ASSEMBLY=Corridor.Portal \
      --build-arg SERVICE_PORT=5200 \
      -t corridor-portal:rhel9 .

The image runs as uid 1001, exposes the requested port and needs the same
environment variables as the unit files (`-e ConnectionStrings__Corridor=...`
and friends). RHEL systems can serve the containers with systemd using
`podman run` wrapped in a unit, or via Quadlets; the units in this directory
show the environment contract either way.
