#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "شغّل المثبّت هكذا: sudo ./install.sh" >&2
  exit 1
fi

if [[ -z "${SUDO_USER:-}" || "${SUDO_USER}" == "root" ]]; then
  echo "شغّل المثبّت من حسابك باستخدام sudo، وليس من جلسة root مباشرة." >&2
  exit 1
fi

user_name="${SUDO_USER}"
user_id="$(id -u "${user_name}")"
user_home="$(getent passwd "${user_name}" | cut -d: -f6)"
if [[ -z "${user_home}" || ! "${user_name}" =~ ^[a-zA-Z0-9_.-]+$ ]]; then
  echo "تعذر التحقق من حساب المستخدم." >&2
  exit 1
fi

package_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
if [[ ! -f "${package_dir}/app/StudyGuard.Linux" || ! -f "${package_dir}/agent/StudyGuard.Agent" ]]; then
  echo "ملفات البرنامج غير مكتملة. فك ملف StudyGuard-Linux-x64.tar.gz كاملًا أولًا." >&2
  exit 1
fi

install -d -o root -g root -m 0755 /opt/studyguard/app /opt/studyguard/agent
cp -a "${package_dir}/app/." /opt/studyguard/app/
cp -a "${package_dir}/agent/." /opt/studyguard/agent/
chmod +x /opt/studyguard/app/StudyGuard.Linux /opt/studyguard/agent/StudyGuard.Agent
chmod -R go-w /opt/studyguard

cat > /etc/systemd/system/studyguard-agent.service <<UNIT
[Unit]
Description=StudyGuard Linux activity protection
After=local-fs.target

[Service]
Type=simple
ExecStart=/opt/studyguard/agent/StudyGuard.Agent --config=${user_home}/.config/studyguard/config.json --uid=${user_id}
Restart=always
RestartSec=2
NoNewPrivileges=false
ProtectHome=read-only
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT

install -d -o "${user_name}" -g "$(id -gn "${user_name}")" -m 0755 "${user_home}/.local/share/applications"
cat > "${user_home}/.local/share/applications/studyguard.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=StudyGuard
Comment=تنظيم وقتك وحجب البرامج والمواقع
Exec=/opt/studyguard/app/StudyGuard.Linux
Terminal=false
Categories=Utility;Education;
DESKTOP
chown "${user_name}:$(id -gn "${user_name}")" "${user_home}/.local/share/applications/studyguard.desktop"
chmod 0644 "${user_home}/.local/share/applications/studyguard.desktop"

systemctl daemon-reload
systemctl enable --now studyguard-agent.service
echo "تم تثبيت StudyGuard وتشغيل وكيل الحماية. افتح StudyGuard من قائمة التطبيقات."
