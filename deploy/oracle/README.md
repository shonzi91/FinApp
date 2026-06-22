# Deploy FinApp on Oracle Cloud (Always Free) — $0 forever

Runs the whole app in one container behind Caddy (free auto-HTTPS) on an Always-Free VM, with
SQLite on a persistent Docker volume. Total cost: **$0**. One-time setup: ~30 minutes.

## Overview
```
Browser ──HTTPS──▶ Caddy (:443, auto Let's Encrypt) ──▶ FinApp container (:8080) ──▶ /data SQLite volume
```

## 1. Create the free VM
1. Sign up at <https://cloud.oracle.com> (the **Always Free** resources need no payment, but a card is
   used for identity verification).
2. **Compute → Instances → Create instance.**
   - **Shape:** `VM.Standard.A1.Flex` (Ampere/ARM Always-Free — give it **2 OCPU / 12 GB**), or the
     `VM.Standard.E2.1.Micro` (AMD, 1 GB) if A1 capacity is unavailable in your region.
   - **Image:** Canonical **Ubuntu 24.04** (or 22.04).
   - **SSH keys:** upload/download a key pair so you can log in.
   - Leave it on the default public subnet with a **public IPv4**.
3. After it boots, note the **public IP**.

## 2. Open ports 80 + 443 in Oracle's cloud firewall
Oracle blocks inbound by default in **two** places — the cloud Security List *and* the OS firewall.
- **Networking → Virtual Cloud Networks → (your VCN) → Security Lists → Default Security List →
  Add Ingress Rules:** for both **TCP 80** and **TCP 443**, source CIDR `0.0.0.0/0`.
- The OS firewall is handled by `setup.sh` below.

## 3. Point a free domain at the VM (needed for HTTPS)
Let's Encrypt issues certs for a **hostname**, not a bare IP. Easiest free option:
1. Go to <https://www.duckdns.org>, sign in, create a subdomain e.g. `finapp`.
2. Set its IP to your VM's public IP → you now have `finapp.duckdns.org`.

(Any domain works — if you own one, just create an `A` record to the VM IP.)

## 4. Install Docker + open the OS firewall
SSH in (`ssh -i your-key ubuntu@<public-ip>`), then:
```bash
sudo apt-get update -y && sudo apt-get install -y git
git clone https://github.com/shonzi91/FinApp.git
cd FinApp/deploy/oracle
chmod +x setup.sh && ./setup.sh
# log out and back in so your user joins the 'docker' group
```

## 5. Configure secrets and launch
```bash
cd ~/FinApp/deploy/oracle
cp .env.example .env
nano .env            # DOMAIN=finapp.duckdns.org   JWT_KEY=<paste output of: openssl rand -base64 48>
docker compose up -d --build
```
First boot builds the image (a few minutes) and Caddy fetches the TLS cert. Watch it:
```bash
docker compose logs -f
```
Then open **https://finapp.duckdns.org** — create your account and you're live.

## Day-2 operations
| Task | Command (in `deploy/oracle`) |
| --- | --- |
| Update to latest code | `git pull && docker compose up -d --build` |
| View logs | `docker compose logs -f` |
| Stop / start | `docker compose down` / `docker compose up -d` |
| **Back up the DB** | `docker compose cp finapp:/data/finapp-server.db ./backup-$(date +%F).db` |
| Rotate the JWT key | edit `.env`, `docker compose up -d` (invalidates existing logins) |

## Notes
- **One instance only** — SQLite is a single file; don't run multiple replicas. Fine for personal/family use.
- The account snapshot is stored **plaintext** server-side (E2E encryption is a later hardening item).
- Keep the `caddy_data` volume — it holds your issued certificates.
