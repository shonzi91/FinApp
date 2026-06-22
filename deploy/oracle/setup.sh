#!/usr/bin/env bash
# One-time host prep for a fresh Oracle Cloud Ubuntu VM (22.04 / 24.04).
# Installs Docker + the compose plugin and opens the OS firewall for HTTP/HTTPS.
# Oracle's *cloud* firewall (VCN Security List / NSG) must also allow 80 + 443 ingress —
# that's done in the Oracle web console, see README.md.
set -euo pipefail

echo "==> Installing Docker…"
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker "$USER"

echo "==> Opening the OS firewall for ports 80 and 443…"
# Oracle Ubuntu images ship an iptables REJECT rule early in the INPUT chain; insert ACCEPTs above it.
sudo iptables -I INPUT 1 -p tcp --dport 80  -j ACCEPT
sudo iptables -I INPUT 1 -p tcp --dport 443 -j ACCEPT
# Persist across reboots (package is usually preinstalled on Oracle images).
sudo apt-get update -y && sudo apt-get install -y iptables-persistent >/dev/null 2>&1 || true
sudo netfilter-persistent save || true

echo
echo "==> Done. Now LOG OUT and BACK IN (so your user picks up the 'docker' group), then:"
echo "      cd \"\$(dirname \"\$0\")\""
echo "      cp .env.example .env   # edit DOMAIN + JWT_KEY"
echo "      docker compose up -d --build"
