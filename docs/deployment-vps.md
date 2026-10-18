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
# Create a non-root sudo user
adduser harald
usermod -aG sudo harald

# Copy your SSH key to the new user (from your local machine)
# ssh-copy-id harald@<server-ip>

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

From here on, do everything as the `harald` user over SSH, not root.

## 3. Install the .NET 10 runtime

This app targets `net10.0` (see `Intelimensa.Accounts.csproj`). You only need the
**ASP.NET Core runtime** on the server, not the SDK (build happens elsewhere — see
step 6).

As of this writing, Microsoft's `packages.microsoft.com` apt feed for Ubuntu 22.04
(`jammy`) doesn't carry .NET 10 packages yet — `apt-cache search aspnetcore-runtime`
tops out at 8.0. Rather than wait on the feed, use Microsoft's install script, which
downloads the exact runtime build directly instead of going through apt:

```bash
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
sudo ./dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet
rm dotnet-install.sh

sudo ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet
```

Verify: `dotnet --list-runtimes` should show `Microsoft.AspNetCore.App 10.x` and
`Microsoft.NETCore.App 10.x`.

If the apt feed catches up later and you'd rather manage updates via `apt`, the
original approach was:

```bash
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt update
sudo apt install -y aspnetcore-runtime-10.0
```

## 4. Install and configure Nginx

```bash
sudo apt install -y nginx
```

Create `/etc/nginx/sites-available/accounts.intelimensa.com`:

```nginx
server {
    listen 80;
    server_name accounts.intelimensa.com;

    # Release installers are uploaded through the Staff > Releases page; Nginx's 1 MB default
    # body limit would reject them. Keep this >= the app's own 1 GiB cap.
    client_max_body_size 1g;

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

The app trusts `X-Forwarded-For`/`X-Forwarded-Proto` from loopback only (`UseForwardedHeaders` in `Program.cs`), so
keep both `proxy_set_header` lines: they are how the app learns the request was https. Without them cookies lose
their `Secure` flag, HSTS isn't sent and the log fills with "Failed to determine the https port". Outside
Development the auth and antiforgery cookies are `Secure` regardless. The http-to-https redirect for the public
is nginx's job (certbot adds it), not the app's.

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
rsync -avz --delete ./publish/ harald@accounts.intelimensa.com:/var/www/accounts/app/
```

On the server, create the app directory and a persistent data directory for the
SQLite database (kept separate from the app bundle so redeploys don't touch it):

```bash
sudo mkdir -p /var/www/accounts/app /var/www/accounts/data
sudo chown -R harald:harald /var/www/accounts
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
- **`Email:*`** — SMTP for the account-confirmation email. `Email:Host` (port 587 STARTTLS by default),
  `Email:User`, `Email:Password` (secret), `Email:FromAddress`, optional `Email:FromName`. Use a dedicated
  mailbox such as `no-reply@intelimensa.com` on the domain's own mail server (cPanel/Exim, port 587 reachable from
  the VPS): its SPF/DKIM cover mail sent through it, whereas mail sent from the VPS directly would fail SPF. The app
  refuses to start if `Email:Host` is set without the rest. With no host, mail is only logged and new accounts can't
  confirm. The public links come from `App:PublicBaseUrl` (already set in `appsettings.Production.json`).
  Locally: `dotnet user-secrets set "Email:Password" ...` etc.
- **`Releases:SigningPublicKey`** — the public half of the release-signing key
  (base64; not a secret, but it must be set). To cover a second signing key (a backup
  YubiKey), either separate keys with `;` here or set `Releases__SigningPublicKeys__0`,
  `__1`, ...; a signature from any one of them is accepted, and the same keys must be
  compiled into AxoSync (`UpdateSigningKeys.txt`). While it is set the server refuses unsigned
  or wrongly signed builds and won't publish a release with an unsigned artifact. Leaving
  it unset disables that check, so don't in production. See
  [`release-publishing.md`](release-publishing.md) for generating the pair. **Production
  currently uses the YubiKey's PIV slot 82 key** (certificate "AxoSync release signing (test)",
  ECDSA P-256). Its public key, exported with `ykman piv keys export 82 -` and converted with
  `openssl pkey -pubin -outform DER | base64`, is
  `MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE7+bra9jiGPJu366y6bBZTivhOE+16lef4JDUnSpPwN28KQZC+Sp0WXcmWDW5egW1S4RniLgOdhyI3MZNYeyXXA==`.
  It's a test key: replacing it means changing this setting *and* the key compiled into AxoSync.

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
Type=simple
WorkingDirectory=/var/www/accounts/app
# Use the path `which dotnet` prints; on the production VPS it is /usr/local/bin/dotnet.
ExecStart=/usr/bin/dotnet /var/www/accounts/app/Intelimensa.Accounts.dll
Restart=always
RestartSec=5
User=harald
Group=harald

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=Jwt__SigningKey=REPLACE_WITH_GENERATED_BASE64_KEY
Environment="ConnectionStrings__DefaultConnection=Data Source=/var/www/accounts/data/accounts.db"
# Uploaded release binaries. Must be outside /var/www/accounts/app (rsync --delete would wipe it).
Environment=Releases__StoragePath=/var/www/accounts/data/releases
Environment=Releases__SigningPublicKey=REPLACE_WITH_PUBLIC_KEY_BASE64
# Outgoing mail for account confirmation (the host's cPanel SMTP; see section 7). Without Email__Host
# nothing is sent, only logged.
Environment=Email__Host=REPLACE_WITH_SMTP_HOST
Environment=Email__User=no-reply@intelimensa.com
Environment=Email__Password=REPLACE_WITH_MAILBOX_PASSWORD
Environment=Email__FromAddress=no-reply@intelimensa.com
# Firmware hex catalog for the manufacturing station; same rule.
Environment=Firmware__StoragePath=/var/www/accounts/data/firmware-files

SyslogIdentifier=intelimensa-accounts

[Install]
WantedBy=multi-user.target
```

Two gotchas that bit during the first real deploy, worth knowing about rather than
rediscovering:

- **`Type=notify` hangs and times out.** It tells systemd to wait for a `READY=1`
  signal via `sd_notify` before considering the service up — but this app never
  calls `.UseSystemd()`, so that signal never arrives. The app itself starts fine
  (you'll see "Application started" in the logs), but systemd kills it ~90s later
  as a timeout anyway. `Type=simple` doesn't wait for any such signal.
- **Every setting line needs the `Environment=` prefix.** A bare `Firmware__StoragePath=...`
  line is ignored by systemd with no error at start-up, and the app silently falls back to its
  default (a folder inside the app directory). Check what the running app actually got with
  `systemctl show -p Environment intelimensa-accounts`.
- **Quote any `Environment=` value that contains a space.** systemd's `Environment=`
  splits on whitespace unless the whole `KEY=VALUE` assignment is wrapped in double
  quotes. The connection string's `Data Source=...` has a space in it — without the
  quotes shown above, systemd silently splits it into two bogus environment
  variables (`ConnectionStrings__DefaultConnection=Data` and
  `Source=/var/www/.../accounts.db`), and the app crashes on startup with `System
  .ArgumentException: Format of the initialization string does not conform to
  specification`.

Enable and start it. (Until the service goes public, production is deliberately left
**disabled**: it's started and stopped by hand and doesn't come back after a reboot. In that
case use `start` instead of `enable --now`.)

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now intelimensa-accounts
sudo systemctl status intelimensa-accounts
journalctl -u intelimensa-accounts -f   # tail logs
```

## 9. Apply EF Core migrations against the production database

**This step is for the first deploy only**, when the database doesn't exist yet. After
that, `scripts/deploy.sh` applies migrations (step 12). Don't re-run the command below
against an existing database: it scripts every migration from the first one, so it
fails on tables that already exist, and `sqlite3` carries on past errors unless told
not to (`-bail`), which can leave a migration half-applied.

Since the SDK isn't on the server, generate a SQL script on your dev machine and apply
it remotely:

```bash
# From Intelimensa.Accounts/ locally
dotnet ef migrations script -o migrate.sql

# Copy and apply on the server (sqlite3 CLI)
scp migrate.sql harald@accounts.intelimensa.com:/tmp/
ssh harald@accounts.intelimensa.com
sqlite3 /var/www/accounts/data/accounts.db < /tmp/migrate.sql
```

On first deploy this also creates the `accounts.db` file — confirm the `harald`
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

Neither the `Staff` nor `Manufacturer` role has a UI to grant it (see "Staff area"
and "Device manufacturing & registration codes" in `CLAUDE.md`) — both are seeded
automatically at startup, but promoting an account into one is a manual step.
`scripts/setrole.sh` in this repo does that against the production DB directly,
rather than hand-writing `INSERT`/`DELETE` SQL each time.

Admin scripts like this one live in `/var/www/accounts/scripts` on the server —
a sibling of `app/` and `data/`, so `rsync --delete` on app redeploys (step 12)
never touches them. `/usr/local/bin` holds symlinks for the short command names;
the scripts themselves are versioned here in the repo. Set up the directory once:

```bash
# On the server
sudo mkdir -p /var/www/accounts/scripts
sudo chown harald:harald /var/www/accounts/scripts
```

Then, to deploy this (or any future) script under `scripts/`:

```bash
# Local — syncs the whole scripts/ folder; re-run this whenever a script changes
rsync -avz --delete scripts/ harald@accounts.intelimensa.com:/var/www/accounts/scripts/

# On the server
chmod +x /var/www/accounts/scripts/*.sh
sudo ln -sf /var/www/accounts/scripts/setrole.sh /usr/local/bin/setrole
```

It defaults to `/var/www/accounts/data/accounts.db` (override with `$SETROLE_DB`
if ever needed) and is idempotent — granting a role someone already has, or
revoking one they don't, is a no-op rather than an error.

After registering a normal account through the web portal at
`https://accounts.intelimensa.com/Account/Register`, promote it:

```bash
setrole --account someone@example.com --role Staff --enabled
```

Remember: role claims are baked into the auth cookie at sign-in, so that user must
log out and back in before the `/Staff` area becomes visible to them.

The `Manufacturer` role (gates the `/api/manufacturing/*` endpoints used by the
manufacturing station app) works the same way:

```bash
setrole --account someone@example.com --role Manufacturer --enabled
```

The `Publisher` role (gates `/api/publishing/*`, the API the `publish.sh` release script
uses) is granted the same way. Give it to a dedicated account for the build machine
rather than to your own login:

```bash
setrole --account publisher@example.com --role Publisher --enabled
```

Unlike `Staff`, JWTs (not just the cookie) carry this role, so a client holding an
already-issued access token won't see the grant until it refreshes. To revoke
any of these roles, swap `--enabled` for `--disabled`.

## 12. Redeploying after code changes

Use `scripts/deploy.sh` from your dev machine, on a committed checkout:

```bash
scripts/deploy.sh plan       # read-only: what is deployed, what would change, pending migrations
scripts/deploy.sh deploy     # asks before touching the server
scripts/deploy.sh status
scripts/deploy.sh rollback   # previous version back, if the new one misbehaves
```

### Checklist

**Once per machine.** The script connects as `harald@accounts.intelimensa.com`, so your
`~/.ssh/config` must give that name the server key, for example:

```
Host accounts-vps accounts.intelimensa.com
    HostName 94.176.105.208
    User harald
    IdentityFile ~/.ssh/intelimensa-accounts-deploy
```

Without the second name on the `Host` line, ssh doesn't offer that key and the connection is
refused (`Permission denied (publickey)`). Check with `ssh harald@accounts.intelimensa.com true`.

**Every deploy:**

1. **Commit.** `deploy` refuses uncommitted changes (`--allow-dirty` overrides, and the deployed
   version is then marked as having uncommitted changes).
2. **Unlock the ssh key** if it has a passphrase and you've rebooted since:
   `ssh-add ~/.ssh/intelimensa-accounts-deploy`.
3. **`scripts/deploy.sh plan`.** Read-only. Shows the deployed commit, the commit to deploy and
   the pending migrations, and checks the server (`sqlite3`, `curl`, `rsync` installed, no data
   inside the app directory).
4. **Check whether the new version needs a setting the server doesn't have yet.** Look through
   the changes since the deployed commit for new configuration (new `Environment=` lines in step
   8, new options in `appsettings.json` without a usable default, new storage paths). Add them to
   the unit *before* deploying, on the server:

   ```bash
   sudo systemctl edit --full intelimensa-accounts   # add the Environment= line(s)
   sudo systemctl daemon-reload
   systemctl show -p Environment intelimensa-accounts  # confirm they're really there
   ```

   The deploy restarts the service, so the new settings take effect with the new version.
5. **`scripts/deploy.sh deploy`, in your own terminal.** It asks for confirmation, and `sudo` on
   the server asks for your password. It must be a real terminal, because of those prompts.
6. **Check:** `scripts/deploy.sh status`, then open `https://accounts.intelimensa.com`.
7. **While the service is kept disabled** (not public yet): the deploy *starts* it, so stop it
   again when you've finished looking: `sudo systemctl stop intelimensa-accounts`.

### What the script does

What `deploy` does, in order:

1. Refuses if there are uncommitted changes (`--allow-dirty` overrides), if the server has run a
   migration this checkout doesn't have (you'd be deploying older code), or if the app directory
   holds data (a database, `releases/`, `firmware-files/`) that switching versions would lose.
2. Publishes a Release build and uploads it to `/var/www/accounts/app.new`, next to the running
   version. The service is still up.
3. Compares the server's `__EFMigrationsHistory` with `Migrations/` and generates a SQL script for
   **only the migrations the server hasn't run**.
4. Stops the service and backs the database up to
   `/var/www/accounts/backups/accounts.pre-deploy-<UTC time>.db`.
5. Applies the script with `sqlite3 -bail`. If anything fails, it restores the backup and starts
   the old version again; nothing is left half-applied.
6. Renames `app` to `app.prev` and `app.new` to `app`, starts the service and waits up to 30
   seconds for it to answer on `http://127.0.0.1:5000/`. If it doesn't, it prints the service log
   and stops there, leaving the new version in place for you to look at or roll back.

`rollback` puts `app.prev` back. If the deploy it undoes changed the schema, it also restores the
pre-deploy database, because old code can't run against the new schema: **anything written since
that deploy is then gone from the live database** (the replaced database is kept in `backups/` as
`accounts.pre-rollback-<time>.db`). If the deploy had no migrations, only the code goes back. Only
one previous version is kept, so a rollback can't be repeated.

Every deploy and rollback appends one line to `backups/deploy.log` on the server: time (UTC), user,
`deploy` or `rollback`, the commit, the migrations applied (or whether the database was restored),
the backup file, and `service=up` or `service=DOWN`. Read it with `scripts/deploy.sh log [N]`. It
starts with the first deploy made after this was added; earlier ones are only in `deploy-info.txt`.

Notes:

- `sudo` on the server is used for `systemctl` and `journalctl` and may ask for your password once
  per run.
- The script writes `deploy-info.txt` (commit and time) into the app directory; `status` reads it.
- Pre-deploy backups are not pruned. Delete old ones from `/var/www/accounts/backups` now and then.
- The server, paths and service name are the defaults at the top of the script
  (`DEPLOY_HOST`, `DEPLOY_ROOT`, `DEPLOY_SERVICE`, `DEPLOY_HEALTH_URL`, `DEPLOY_HEALTH_HOST` override them).
  The health check sends `Host: accounts.intelimensa.com`, because production's `AllowedHosts`
  answers any other host with a 400.

## 13. Backups

The whole persistence layer is one file: `/var/www/accounts/data/accounts.db`.
A simple cron-based daily copy is enough at this scale:

```bash
# /etc/cron.d/accounts-backup
0 3 * * * harald sqlite3 /var/www/accounts/data/accounts.db ".backup /var/www/accounts/backups/accounts-$(date +\%F).db"
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
- **`AllowedHosts`** — `appsettings.json`'s `"*"` stays as the dev/test default;
  `Intelimensa.Accounts/appsettings.Production.json` overrides it to
  `accounts.intelimensa.com` for the Production environment only (ASP.NET Core
  layers `appsettings.{Environment}.json` automatically — no systemd change
  needed, since this isn't a secret). Resolved once the real domain went live.
- **Monitoring/alerting** isn't covered above — at minimum, consider a simple
  uptime check (e.g. an external ping service) against
  `https://accounts.intelimensa.com`, since `systemctl enable --now` + `Restart=always`
  only protects against process crashes, not the VPS itself going down.
- **`Manufacturing:ProductCodes`/`RegionCodes` in `appsettings.json`**: product
  codes are the firmware families (`msv1`/`msv2`/`msv3` → `MSV1`/`MSV2`/`MSV3`),
  region is `G` only. Confirm the region list matches what you intend to issue
  serials under before the manufacturing API is used for anything real.
