# StudyGuard for Linux

Arabic, dark RTL desktop application for Linux x64, with a systemd agent for scheduled and permanent app/site blocking.

## Build

The GitHub Actions workflow builds a self-contained Ubuntu/Debian x64 archive. It is available as a workflow artifact after the workflow completes.

## Install

Extract `StudyGuard-Linux-x64.tar.gz`, open the extracted folder in a terminal, and run:

```sh
sudo bash ./install.sh
```

The installer registers the background systemd service and adds StudyGuard to the desktop applications menu. Launch StudyGuard from the menu. User settings stay in `~/.config/studyguard/config.json`.

To remove the system service and app, run `sudo bash ./uninstall.sh` from the extracted package. User settings are retained.

## Scope and limitations

- Targets Linux x64 desktops using systemd and X11 or Wayland through XWayland.
- Website rules use `/etc/hosts`; encrypted DNS, VPNs, and some applications may bypass host-file rules.
- App blocking matches Linux process names for the logged-in user; the agent sends SIGTERM and then SIGKILL after four seconds.
- Sleep/shutdown enforcement and Windows feature parity are not included in this Linux build.
