# SSH access setup — Windows

Goal: generate an SSH keypair on this Windows machine and add its public key to
the `harald` account on the Accounts VPS, so you can manage the server from
here too, alongside the Mac.

## 1. Generate a keypair

Windows 10/11 ships with the OpenSSH client built in. Open **PowerShell** (not
Command Prompt) and run:

```powershell
ssh-keygen -t ed25519 -C "harald-windows"
```

- Accept the default save location (`C:\Users\<you>\.ssh\id_ed25519`) by
  pressing Enter.
- Set a passphrase when prompted — same reasoning as the Mac key: if this
  laptop is ever lost or stolen, the key file alone shouldn't be enough to
  reach the server.

This creates two files:
- `id_ed25519` — the **private** key. Never copy it off this machine, never
  paste it anywhere, never email it.
- `id_ed25519.pub` — the **public** key. Safe to share/paste; this is what
  goes on the server.

## 2. Get the public key text

```powershell
Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub
```

Copy the single line of output (starts with `ssh-ed25519 AAAA...`).

## 3. Add it to the server — from the Mac

You can't SSH in from Windows yet (no key is on the server for it, and
password login is disabled entirely), so this step has to be done from a
machine that already has access — the Mac.

On the **Mac**, SSH into the server as usual, then append the Windows public
key to `harald`'s `authorized_keys` (replace the placeholder with what you
copied in step 2):

```bash
ssh harald@accounts.intelimensa.com
echo 'ssh-ed25519 AAAA...your-windows-key... harald-windows' >> ~/.ssh/authorized_keys
```

Double-check you used `>>` (append), not `>` (overwrite) — the file should
end up with two lines, one per machine. You can confirm with:

```bash
cat ~/.ssh/authorized_keys
```

## 4. (Optional) Add a convenience SSH config entry on Windows

Create/edit `C:\Users\<you>\.ssh\config`:

```
Host accounts-vps
    HostName accounts.intelimensa.com
    User harald
    IdentityFile ~/.ssh/id_ed25519
```

Then you can just run `ssh accounts-vps` instead of typing the full
`user@host` every time.

## 5. (Optional) Avoid retyping the passphrase every time

Windows has a built-in `ssh-agent` service, off by default. Enable it once
(PowerShell **as Administrator**):

```powershell
Get-Service ssh-agent | Set-Service -StartupType Automatic
Start-Service ssh-agent
```

Then add your key to it per session:

```powershell
ssh-add $env:USERPROFILE\.ssh\id_ed25519
```

## 6. Test it

```powershell
ssh accounts-vps
```

(or `ssh harald@accounts.intelimensa.com` if you skipped step 4). You should
land on the server as `harald`, prompted only for your key's passphrase (if
you set one) — never a server-side password, since password authentication
is disabled entirely now.
