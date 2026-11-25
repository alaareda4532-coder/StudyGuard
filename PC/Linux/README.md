# StudyGuard for Linux

Arabic, dark RTL desktop application for Linux x64, with a systemd agent for scheduled and permanent app/site blocking.

## Build

Run `./package/build-release.sh` from `PC/Linux` to build the self-contained
tar archive and Debian package in `Release/0.3.0/`. The package stages files
under `/opt/studyguard/packages/0.3.0` and does not automatically replace or
start an existing installation. The GitHub Actions workflow runs the focused
agent tests and builds both packages as workflow artifacts.

## Install

Install a Debian package:

```sh
sudo apt install ./studyguard-linux_0.3.0_amd64.deb
sudo /opt/studyguard/packages/0.3.0/install.sh
```

To switch an already installed StudyGuard service, explicitly activate the new
version with `sudo /opt/studyguard/packages/0.3.0/install.sh --activate`.
Alternatively, extract the tar archive and run its `install.sh`. Launch
StudyGuard from the desktop applications menu. User settings stay in
`~/.config/studyguard/config.json`.

To roll back to the legacy `/opt/studyguard/app` and `/opt/studyguard/agent`
installation, run `sudo /opt/studyguard/packages/0.3.0/rollback.sh`. To remove
version 0.3.0, run its `uninstall.sh`, then `sudo dpkg -r studyguard-linux`.
Settings and the legacy install are retained.

## Scope and limitations

- Targets Linux x64 desktops using systemd and X11 or Wayland through XWayland.
- While YouTube is blocked, the agent configures the GNOME HTTP/HTTPS desktop proxy and excludes YouTube from its managed `/etc/hosts` entries only after the proxy is active. The proxy maps connections to their owning PID using `/proc/net/tcp[6]` socket inodes and `/proc/PID/fd`, then checks `/proc/PID/exe` and the yt-dlp entrypoint path.
- The yt-dlp exception accepts executable files at `/usr/bin/yt-dlp`, `/usr/local/bin/yt-dlp`, or `~/.local/bin/yt-dlp`, and only for YouTube, YouTube API, youtu.be, and googlevideo hosts. Other scheduled and permanent site rules continue to use `/etc/hosts` and the proxy policy.
- The YouTube process exception requires GNOME's `org.gnome.system.proxy` settings schema, `gsettings`, `runuser`, and an active GNOME user session. On other desktop environments, or before the session bus is ready, the agent fails closed by keeping YouTube in its managed hosts entries, so yt-dlp will not be exempted.
- This GNOME desktop proxy is not a firewall: browsers or applications that bypass desktop proxy settings, QUIC/UDP 443, VPNs, encrypted DNS, or direct IP access can bypass some rules. This build does not claim network-wide enforcement. Use a firewall for stronger enforcement.
- If the proxy cannot be applied, YouTube remains blocked in managed `/etc/hosts` entries (yt-dlp then cannot use the exception). On removal, if previous GNOME settings cannot be restored, the uninstaller retains/restarts protection and refuses cleanup.
- App blocking matches Linux process names for the logged-in user; the agent sends SIGTERM and then SIGKILL after four seconds.
- Sleep/shutdown enforcement and Windows feature parity are not included in this Linux build.
