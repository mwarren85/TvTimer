# TvTimer

A small .NET worker service that powers off a Sony Bravia TV on a schedule,
using the TV's local IP control REST API. Designed to run on a Raspberry Pi
under systemd, but runs anywhere .NET does.

Tested on the Bravia XR-65A95L.

## TV setup

1. Give the TV a static IP (or a DHCP reservation on your router).
2. On the TV, go to **Settings → Network & Internet → Home network setup →
   IP control** (wording varies by firmware) and set **Authentication** to
   either:
   - **Normal and Pre-Shared Key** (recommended): set a key of your choice.
   - **Normal**: uses one-time PIN pairing instead.
3. Enable **Settings → Network & Internet → Remote device settings → Control
   remotely**.

## Configuration

Edit `appsettings.json`:

| Setting | Description |
|---|---|
| `Sony:TvIp` | The TV's IP address |
| `Sony:Psk` | Pre-shared key. Leave empty to use PIN pairing |
| `Sony:AuthCookiePath` | Where the PIN pairing cookie is stored |
| `Sony:UseHttps` | Connect over HTTPS instead of HTTP |
| `Sony:CaCertPath` | TV's exported certificate, used to pin trust when `UseHttps` is true |
| `Schedule:CronExpression` | 5-field cron (`minute hour day month day-of-week`) |
| `Schedule:TimeZoneId` | IANA time zone, e.g. `Europe/London` |

Example schedules:

| Schedule | Cron |
|---|---|
| 10:30pm Sun–Thu | `30 22 * * 0-4` |
| 11pm every night | `0 23 * * *` |
| 9:45pm Mon–Fri | `45 21 * * 1-5` |

## Build

Requires the .NET 10 SDK. Publish a self-contained single-file build so the
target doesn't need the .NET runtime:

```bash
# 64-bit Raspberry Pi OS (aarch64)
dotnet publish src/TvTimer -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o publish/linux-arm64

# 32-bit Raspberry Pi OS (armv7l)
dotnet publish src/TvTimer -c Release -r linux-arm --self-contained -p:PublishSingleFile=true -o publish/linux-arm
```

## Install

Create a service user and the directories the service uses, then copy the
publish output to `/opt/tvtimer`:

```bash
sudo useradd --system --no-create-home tvtimer
sudo mkdir -p /opt/tvtimer /var/lib/tvtimer /var/log/tvtimer
sudo chown tvtimer:tvtimer /opt/tvtimer /var/lib/tvtimer /var/log/tvtimer
```

Keep your own settings in `appsettings.Production.json` next to
`appsettings.json` (git-ignored). It overrides the defaults when running in
Production.

### Authenticate

```bash
cd /opt/tvtimer
sudo -u tvtimer ./TvTimer --pair
```

- **PSK:** verifies the key against the TV and exits.
- **PIN:** the TV shows a 4-digit PIN; enter it when prompted. The auth
  cookie is saved to `Sony:AuthCookiePath` and reused from then on.

### Run as a service

The unit in `systemd/tvtimer.service` runs `/opt/tvtimer/TvTimer` as the
`tvtimer` user. Adjust if your paths differ, then:

```bash
sudo cp systemd/tvtimer.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now tvtimer
```

Stop the service before overwriting the binary when redeploying. Config-only
changes just need `sudo systemctl restart tvtimer`.

## HTTPS

Some firmware only allows IP control over HTTPS. To use it, export the TV's
certificate from its IP control settings, copy it next to the app, and set
`Sony:UseHttps` to `true` and `Sony:CaCertPath` to the file. The certificate
is self-signed, so TvTimer trusts only that exact certificate. Re-export it
if the TV is factory reset.

## Logging

Logs go to stdout (`journalctl -u tvtimer -f`) and to daily rolling files in
`/var/log/tvtimer/`, kept for 14 days. Configure this in the `Serilog`
section of `appsettings.json`. In Development, logs go to `./logs/`.

## Troubleshooting

- **`403 Forbidden` (nginx page) before any PIN appears:** check that
  *Control remotely* is enabled, and that the HTTP/HTTPS setting on the TV
  matches `Sony:UseHttps`. If PIN pairing still fails, use a PSK.
- **Auth errors after a factory reset:** re-run `./TvTimer --pair` (PIN) or
  update `Sony:Psk` (PSK), then restart the service.
