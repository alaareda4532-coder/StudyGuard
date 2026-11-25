#!/usr/bin/env bash
set -euo pipefail

root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
cd "${root}"

version="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' StudyGuard.Agent/StudyGuard.Agent.csproj)"
app_version="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' StudyGuard.Linux/StudyGuard.Linux.csproj)"
if [[ -z "${version}" || "${version}" != "${app_version}" ]]; then
  echo "Linux app and agent versions must be present and match." >&2
  exit 1
fi
if ! command -v dpkg-deb >/dev/null; then
  echo "dpkg-deb is required to build the Debian package." >&2
  exit 1
fi

release_dir="${root}/Release/${version}"
mkdir -p "${release_dir}"
test_log="${release_dir}/TEST-RESULTS.txt"
dotnet run --project StudyGuard.Agent.Tests/StudyGuard.Agent.Tests.csproj -c Release | tee "${test_log}"
cat >> "${test_log}" <<'EOF'

Not run in this Codespaces container:
- GNOME session proxy application/restoration (no GNOME session bus).
- systemd service start/stop/restart and reboot persistence (systemd is not running).
- browser GUI enforcement and real yt-dlp download (no desktop/browser session).
- Debian package installation/removal (requires root; package contents are checked separately).

Known enforcement boundary:
- The selected GNOME desktop proxy can be bypassed by direct connections. QUIC/UDP 443,
  VPNs, encrypted DNS, direct-IP access, and applications that ignore the system proxy
  are not guaranteed to be blocked.
EOF

stage="$(mktemp -d)"
trap 'rm -rf -- "${stage}"' EXIT
app_dir="${stage}/StudyGuard-Linux-x64"
mkdir -p "${app_dir}/app" "${app_dir}/agent"

dotnet publish StudyGuard.Linux/StudyGuard.Linux.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "${app_dir}/app"
dotnet publish StudyGuard.Agent/StudyGuard.Agent.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "${app_dir}/agent"

cp package/install.sh package/uninstall.sh package/rollback.sh package/README.txt "${app_dir}/"
chmod +x "${app_dir}/install.sh" "${app_dir}/uninstall.sh" "${app_dir}/rollback.sh"
find "${app_dir}" -type d -exec chmod 0755 {} +
find "${app_dir}" -type f -exec chmod 0644 {} +
chmod +x "${app_dir}/app/StudyGuard.Linux" "${app_dir}/agent/StudyGuard.Agent"
tar -czf "${release_dir}/StudyGuard-Linux-x64.tar.gz" -C "${stage}" StudyGuard-Linux-x64

deb_root="${stage}/deb-root"
package_dir="${deb_root}/opt/studyguard/packages/${version}"
install -d "${deb_root}/DEBIAN" "${package_dir}/app" "${package_dir}/agent" \
  "${deb_root}/usr/share/doc/studyguard-linux"
cp -a "${app_dir}/app/." "${package_dir}/app/"
cp -a "${app_dir}/agent/." "${package_dir}/agent/"
cp package/install.sh package/uninstall.sh package/rollback.sh package/README.txt "${package_dir}/"
cp package/README.txt "${deb_root}/usr/share/doc/studyguard-linux/README.txt"
find "${deb_root}" -type d -exec chmod 0755 {} +
find "${deb_root}" -type f -exec chmod 0644 {} +
chmod +x "${package_dir}/install.sh" "${package_dir}/uninstall.sh" "${package_dir}/rollback.sh"
chmod +x "${package_dir}/app/StudyGuard.Linux" "${package_dir}/agent/StudyGuard.Agent"
cat > "${deb_root}/DEBIAN/control" <<CONTROL
Package: studyguard-linux
Version: ${version}
Section: utils
Priority: optional
Architecture: amd64
Depends: systemd, libfontconfig1, libglib2.0-bin, gsettings-desktop-schemas, libice6, libsm6, libx11-6, util-linux
Maintainer: StudyGuard
Description: StudyGuard scheduling and website protection for Linux
 Arabic RTL desktop app and systemd protection agent for Ubuntu/Debian x64.
CONTROL
dpkg-deb --build --root-owner-group "${deb_root}" \
  "${release_dir}/studyguard-linux_${version}_amd64.deb"
cp package/README.txt "${release_dir}/README.txt"

package_file="${release_dir}/studyguard-linux_${version}_amd64.deb"
[[ "$(dpkg-deb --field "${package_file}" Version)" == "${version}" ]]
[[ "$(dpkg-deb --field "${package_file}" Architecture)" == "amd64" ]]
package_contents="$(dpkg-deb --contents "${package_file}")"
archive_contents="$(tar -tzf "${release_dir}/StudyGuard-Linux-x64.tar.gz")"
grep -Eq '^-rwxr-xr-x root/root +[0-9]+ .*/app/StudyGuard.Linux$' <<< "${package_contents}"
grep -Eq '^-rwxr-xr-x root/root +[0-9]+ .*/agent/StudyGuard.Agent$' <<< "${package_contents}"
grep -Eq '^-rwxr-xr-x root/root +[0-9]+ .*/install.sh$' <<< "${package_contents}"
grep -Fq "/opt/studyguard/packages/${version}/app/StudyGuard.Linux" <<< "${package_contents}"
grep -Fq "/opt/studyguard/packages/${version}/agent/StudyGuard.Agent" <<< "${package_contents}"
grep -Fq "/opt/studyguard/packages/${version}/rollback.sh" <<< "${package_contents}"
grep -Fq "StudyGuard-Linux-x64/agent/StudyGuard.Agent" <<< "${archive_contents}"
grep -Fq "StudyGuard-Linux-x64/rollback.sh" <<< "${archive_contents}"
cat >> "${test_log}" <<EOF

PASS: self-contained app and agent published for linux-x64.
PASS: Debian package metadata is version ${version}, architecture amd64.
PASS: Debian package contains the app, agent, and rollback script with executable modes and readable parent directories.
PASS: tar archive contains the agent and rollback script.
NOT RUN: live GNOME/systemd, browser, yt-dlp network download, and install/remove integration checks (see environment limitations above).
EOF

printf 'Release %s built for linux-x64 / Debian amd64.\n' "${version}"
printf 'Artifacts: %s\n' "${release_dir}"
