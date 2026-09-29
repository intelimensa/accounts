# Deploying Intelimensa.Accounts to a VPS (accounts.intelimensa.com)

This is a runbook for standing up `accounts.intelimensa.com` on a plain Linux VPS
(Kestrel + Nginx reverse proxy + systemd), rather than a managed PaaS. Written with
a Hetzner-style EU-owned VPS provider in mind (Hetzner / OVHcloud / Scaleway / IONOS
all work the same way once you have SSH access to an Ubuntu box), but nothing here
is provider-specific beyond step 1.

Target stack: Ubuntu 24.04 LTS, .NET 10 runtime, Nginx, systemd, Let's Encrypt (certbot).

---

## 1. Provision the VPS

- Pick a provider with an EU data center (e.g. Hetzner Falkenstein/Nuremberg/Helsinki,
  OVHcloud Gravelines/Strasbourg, Scaleway Paris).
- Smallest useful size: 1 vCPU / 2GB RAM is plenty for this app's cohort scale
  (Hetzner CX22-class or equivalent). SQLite + a small ASP.NET Core app has a light
  footprint.
- Image: Ubuntu 24.04 LTS.
- Add your SSH public key at creation time instead of a root password.
- Note the server's public IPv4 (and IPv6 if assigned) — you'll need it for DNS.

## 2. Initial server hardening

SSH in as root, then:

```bash
# Create a non-root deploy user
adduser deploy
usermod -aG sudo deploy

# Copy your SSH key to the new user (from your local machine)
# ssh-copy-id deploy@<server-ip>

# Disable root SSH login and password auth
# Edit /etc/ssh/sshd_config:
#   PermitRootLogin no
#   PasswordAuthentication no
sudo systemctl restart ssh

# Firewall: only SSH, HTTP, HTTPS
sudo apt update && sudo apt install -y ufw
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable

# Optional but recommended: fail2ban against SSH brute force
sudo apt install -y fail2ban
```

From here on, do everything as the `deploy` user over SSH, not root.

## 3. Install the .NET 10 runtime

This app targets `net10.0` (see `Intelimensa.Accounts.csproj`). You only need the
**ASP.NET Core runtime** on the server, not the SDK (build happens elsewhere — see
step 6).

```bash
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

sudo apt update
sudo apt install -y aspnetcore-runtime-10.0
```

Verify: `dotnet --list-runtimes` should show `Microsoft.AspNetCore.App 10.x`.

## 4. Install and configure Nginx

```bash
sudo apt install -y nginx
```

Create `/etc/nginx/sites-available/accounts.intelimensa.com`:

```nginx
server {
    listen 80;
    server_name accounts.intelimensa.com;

    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header    Upgrade $http_upgrade;
        proxy_set_header    Connection keep-alive;
        proxy_set_header    Host $host;
        proxy_cache_bypass  $http_upgrade;
        proxy_set_header    X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header    X-Forwarded-Proto $scheme;
    }
}
```

Enable it:

```bash
sudo ln -s /etc/nginx/sites-available/accounts.intelimensa.com /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
```

Port 5000 is arbitrary but matches what the systemd unit below binds Kestrel to.

## 5. DNS — point the subdomain at the server

In whatever DNS provider hosts the `intelimensa.com` zone (the same place the
WordPress marketing site's DNS is managed), add:

| Type | Name | Value | TTL |
|---|---|---|---|
| A | `accounts` | `<server public IPv4>` | 300 (lower while testing, raise later) |
| AAAA | `accounts` | `<server public IPv6>` | 300 | *(only if the VPS has IPv6)* |

This resolves `accounts.intelimensa.com` to the VPS without touching anything on
the WordPress side — the two stay on separate infrastructure per the project's
existing "not a WordPress plugin" design decision.

Wait for propagation (`dig accounts.intelimensa.com` from your local machine) before
requesting a TLS cert in the next step — Let's Encrypt's HTTP challenge needs the
DNS record live and pointed at this server.

**Not ready to expose this publicly yet?** Skip this step for now and see
"Testing before going public" near the end of this doc — you can deploy and test
everything else (steps 6–9) without the real DNS record in place, and come back to
this step + step 10 (TLS) once testing is done.

## 6. Build and publish the app

Do the build on your dev machine (or CI) — don't install the full SDK on the VPS
just to build. From the repo root:

```bash
cd Intelimensa.Accounts
dotnet publish -c Release -o ./publish
```

Copy the output to the server (adjust path/user as needed):

```bash
rsync -avz --delete ./publish/ deploy@accounts.intelimensa.com:/var/www/accounts/app/
```

On the server, create the app directory and a persistent data directory for the
SQLite database (kept separate from the app bundle so redeploys don't touch it):

```bash
sudo mkdir -p /var/www/accounts/app /var/www/accounts/data
sudo chown -R deploy:deploy /var/www/accounts
```

## 7. Configuration and secrets in production

`appsettings.json` ships with non-secret defaults (`Jwt:Issuer`, `Jwt:Audience`,
token lifetimes, `AllowedHosts`). Two things must be overridden in production via
environment variables — ASP.NET Core maps `Section:Key` to `SECTION__KEY`
(double underscore) in the environment:

- **`Jwt:SigningKey`** — required at startup or the app throws (see `Program.cs`).
  Generate one: `openssl rand -base64 64`.
- **`ConnectionStrings:DefaultConnection`** — the default (`Data Source=accounts.db`)
  is a relative path, which resolves against whatever directory the process starts
  in. Point it at the persistent data directory created in step 6 instead.

These go in the systemd unit's `Environment=` lines below (`/etc/systemd/system`
files are root-owned and not world-readable, which is an acceptable place for
secrets on a single-purpose VPS — avoid also committing them anywhere in the repo).

## 8. systemd service

Create `/etc/systemd/system/intelimensa-accounts.service`:

```ini
[Unit]
Description=Intelimensa Accounts
After=network.target

[Service]
Type=notify
WorkingDirectory=/var/www/accounts/app
ExecStart=/usr/bin/dotnet /var/www/accounts/app/Intelimensa.Accounts.dll
Restart=always
RestartSec=5
User=deploy
Group=deploy

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=Jwt__SigningKey=REPLACE_WITH_GENERATED_BASE64_KEY
Environment=ConnectionStrings__DefaultConnection=Data Source=/var/www/accounts/data/accounts.db

SyslogIdentifier=intelimensa-accounts

[Install]
WantedBy=multi-user.target
```

Enable and start it:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now intelimensa-accounts
sudo systemctl status intelimensa-accounts
journalctl -u intelimensa-accounts -f   # tail logs
```

## 9. Apply EF Core migrations against the production database

Since the SDK isn't on the server, run migrations from your dev machine against the
production connection string over SSH, or generate a SQL script and apply it
remotely — the script route avoids needing the dev machine to reach the VPS's
loopback-bound DB file directly:

```bash
# From Intelimensa.Accounts/ locally
dotnet ef migrations script -o migrate.sql

# Copy and apply on the server (sqlite3 CLI)
scp migrate.sql deploy@accounts.intelimensa.com:/tmp/
ssh deploy@accounts.intelimensa.com
sqlite3 /var/www/accounts/data/accounts.db < /tmp/migrate.sql
```

On first deploy this also creates the `accounts.db` file — confirm the `deploy`
user owns `/var/www/accounts/data` (step 6) so the running service can write to it.

## 10. TLS via Let's Encrypt

Once DNS resolves and Nginx is serving plain HTTP on the domain:

```bash
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d accounts.intelimensa.com
```

Certbot edits the Nginx site config to add the 443 server block and redirect
80→443, and installs a systemd timer for auto-renewal. Verify the timer:

```bash
sudo systemctl status certbot.timer
```

## 11. Seed the first Staff user

The `Staff` role is seeded automatically on startup (per `Program.cs`), but no
account holds it yet. After registering a normal account through the web portal
at `https://accounts.intelimensa.com/Account/Register`, promote it manually:

```bash
sqlite3 /var/www/accounts/data/accounts.db <<'SQL'
INSERT INTO AspNetUserRoles (UserId, RoleId)
SELECT u.Id, r.Id FROM AspNetUsers u, AspNetRoles r
WHERE u.Email = 'someone@example.com' AND r.Name = 'Staff';
SQL
```

Remember: role claims are baked into the auth cookie at sign-in, so that user must
log out and back in before the `/Staff` area becomes visible to them.

## 12. Redeploying after code changes

```bash
# Local
cd Intelimensa.Accounts
dotnet publish -c Release -o ./publish
rsync -avz --delete --exclude 'accounts.db' ./publish/ deploy@accounts.intelimensa.com:/var/www/accounts/app/

# Server
ssh deploy@accounts.intelimensa.com
sudo systemctl restart intelimensa-accounts
```

If the change includes a new EF Core migration, generate and apply a new
`migrate.sql` (step 9) before restarting the service.

## 13. Backups

The whole persistence layer is one file: `/var/www/accounts/data/accounts.db`.
A simple cron-based daily copy is enough at this scale:

```bash
# /etc/cron.d/accounts-backup
0 3 * * * deploy sqlite3 /var/www/accounts/data/accounts.db ".backup /var/www/accounts/backups/accounts-$(date +\%F).db"
```

Use `sqlite3 .backup` rather than `cp` — it's safe against concurrent writes from
the running app, unlike a raw file copy. Rotate/prune old backups and, ideally,
sync them off-box (e.g. to the VPS provider's object storage or another server) so
a single-disk failure doesn't take the only copy with it.

---

## Testing before going public

You can have the app fully deployed and running (steps 1–9) without it being
reachable at `accounts.intelimensa.com` yet, or even under any real domain. A few
ways to gate access while you test, roughly in order of how much they're worth
combining:

- **Firewall allowlist (the real gate).** Restrict ports 80/443 to specific source
  IPs instead of the world:

  ```bash
  sudo ufw delete allow 80/tcp
  sudo ufw delete allow 443/tcp
  sudo ufw allow from <your-home-or-office-ip> to any port 80,443 proto tcp
  ```

  This is the actual access control — everything below is just about *what
  hostname/URL* you hit, not *who* can reach it. Widen or remove it once you're
  ready to go public.

- **Hit it by raw IP for quick smoke tests.** With no `server_name` DNS record
  yet, `http://<server-ip>` (or `:5000` direct to Kestrel, bypassing Nginx) works
  immediately — no domain needed at all. Fine for a first "does it even boot"
  check, but no TLS, and cookies/redirects that assume a real hostname may behave
  oddly.

- **Use a wildcard-DNS service for real HTTPS testing without touching
  `intelimensa.com`.** Services like [nip.io](https://nip.io) resolve
  `<server-ip>.nip.io` (e.g. `203.0.113.10.nip.io`) to that IP automatically — no
  DNS record to create anywhere. Point Nginx's `server_name` at that hostname
  temporarily and run `certbot --nginx -d 203.0.113.10.nip.io` to get a real,
  browser-trusted Let's Encrypt cert. This is the closest thing to a true
  staging environment: you get to test HTTPS-dependent behavior (secure cookies,
  redirects, the JWT flow over TLS) before wiring the production domain.

- **Add HTTP Basic Auth as a second layer in Nginx**, if you want defense in depth
  beyond the firewall (e.g. testing from a laptop that roams between networks/IPs):

  ```bash
  sudo apt install -y apache2-utils
  sudo htpasswd -c /etc/nginx/.htpasswd testuser
  ```

  Then inside the `location /` block: `auth_basic "Restricted"; auth_basic_user_file /etc/nginx/.htpasswd;`

**One important caveat:** don't rely on an "unlisted" real subdomain
(`accounts-test.intelimensa.com`) as your only protection. Any certificate Let's
Encrypt issues for a hostname is published to public Certificate Transparency
logs — the moment you request a cert for it, the subdomain itself is discoverable
by anyone watching CT logs, even if it's never linked from anywhere. The firewall
allowlist above is what actually keeps it private; the hostname choice doesn't.

Given the account/config data this app handles is exactly what the project's own
threat model (see CLAUDE.md's "Why this is a separate service" section) is built
around protecting, testing behind an IP allowlist rather than "security by
obscurity" is worth doing even for a short-lived test deploy.

---

## Open questions to resolve before going live

- **DNS provider access** — confirm who/where `intelimensa.com` DNS is managed and
  that you (or whoever handles it) can add the `accounts` A/AAAA record.
- **`AllowedHosts`** in `appsettings.json` is currently `"*"` — fine behind Nginx
  terminating on a known hostname, but worth tightening to
  `accounts.intelimensa.com` once the domain is live, to reject stray Host headers.
- **Monitoring/alerting** isn't covered above — at minimum, consider a simple
  uptime check (e.g. an external ping service) against
  `https://accounts.intelimensa.com`, since `systemctl enable --now` + `Restart=always`
  only protects against process crashes, not the VPS itself going down.
