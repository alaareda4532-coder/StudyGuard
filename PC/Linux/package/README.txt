StudyGuard for Ubuntu/Debian x64 — version 0.3.0

Installation from the Debian package:
1. Install dependencies and the package: sudo apt install ./studyguard-linux_0.3.0_amd64.deb
2. The package only stages a versioned copy; it does not replace or start an
   existing installation automatically.
3. To activate it for the first time:
   sudo /opt/studyguard/packages/0.3.0/install.sh
   To switch from an existing StudyGuard service, explicitly run:
   sudo /opt/studyguard/packages/0.3.0/install.sh --activate

Installation from the tar archive:
1. Extract the archive and run sudo ./install.sh.
2. If another StudyGuard service is already installed, use --activate only
   when intentionally switching versions.

The agent configures the GNOME desktop HTTP/HTTPS proxy while YouTube is
blocked and restores the previous proxy settings when that rule ends or the
service is removed. YouTube is removed from StudyGuard's managed /etc/hosts
entries only after the proxy has been configured successfully. Other managed
website rules remain in /etc/hosts.

The process exception uses the owning PID discovered from /proc/net/tcp and
/proc/PID/fd and checks the executable path. Supported yt-dlp paths are:
  /usr/bin/yt-dlp
  /usr/local/bin/yt-dlp
  ~/.local/bin/yt-dlp
The selected file must exist and have an executable bit. Python module
invocation (python -m yt_dlp) is not an approved path.

This is a desktop-proxy policy, not a firewall. Browsers or applications that
ignore the GNOME proxy can connect directly; QUIC/UDP 443, VPNs, encrypted DNS,
and other bypass paths are not guaranteed to be blocked. Use a network
firewall for stronger enforcement. Flatpak or other isolated applications
may not expose their client socket in the agent's network namespace and will
fail closed for blocked YouTube connections.
GNOME's proxy schema and a live GNOME user session are required for the
YouTube exception. If they are unavailable, YouTube remains blocked via
StudyGuard's managed hosts entries and yt-dlp does not receive an exception.

To roll back to the legacy installation at /opt/studyguard/app and
/opt/studyguard/agent, run:
  sudo /opt/studyguard/packages/0.3.0/rollback.sh
For a tar install, use rollback.sh from the extracted archive.

To remove version 0.3.0, run:
  sudo /opt/studyguard/packages/0.3.0/uninstall.sh
Then, if installed as a Debian package:
  sudo dpkg -r studyguard-linux
The uninstall script retains ~/.config/studyguard and the legacy installation.
If GNOME settings cannot be restored (for example, no desktop session bus is
available), it leaves the service and files in place and asks you to retry
while logged into the desktop.
