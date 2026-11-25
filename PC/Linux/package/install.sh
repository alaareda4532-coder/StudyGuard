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

version="0.3.0"
activate_existing="${1:-}"
existing_service=0
package_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
if [[ ! -f "${package_dir}/app/StudyGuard.Linux" || ! -f "${package_dir}/agent/StudyGuard.Agent" ]]; then
  echo "ملفات البرنامج غير مكتملة. فك ملف StudyGuard-Linux-x64.tar.gz كاملًا أولًا." >&2
  exit 1
fi

if [[ -f /etc/systemd/system/studyguard-agent.service && "${activate_existing}" != "--activate" ]]; then
  echo "توجد خدمة StudyGuard مثبتة. لم يتم استبدالها؛ أعد التشغيل مع --activate للتبديل يدويًا." >&2
  exit 1
fi
if [[ -f /etc/systemd/system/studyguard-agent.service ]]; then
  existing_service=1
fi

release_dir="/opt/studyguard/releases/${version}"
if [[ -e "${release_dir}" ]]; then
  echo "الإصدار ${version} موجود بالفعل في ${release_dir}. لن يتم استبداله." >&2
  exit 1
fi

install -d -o root -g root -m 0755 "${release_dir}/app" "${release_dir}/agent"
cp -a "${package_dir}/app/." "${release_dir}/app/"
cp -a "${package_dir}/agent/." "${release_dir}/agent/"
chmod +x "${release_dir}/app/StudyGuard.Linux" "${release_dir}/agent/StudyGuard.Agent"
chmod -R go-w "${release_dir}"
install -d -o root -g root -m 0700 /var/lib/studyguard

cat > /etc/systemd/system/studyguard-agent.service <<UNIT
[Unit]
Description=StudyGuard Linux activity protection
After=local-fs.target
Wants=graphical.target

[Service]
Type=simple
ExecStart=${release_dir}/agent/StudyGuard.Agent --config=${user_home}/.config/studyguard/config.json --uid=${user_id} --user=${user_name} --home=${user_home} --state-dir=/var/lib/studyguard
ExecStopPost=${release_dir}/agent/StudyGuard.Agent --restore-proxy --uid=${user_id} --user=${user_name} --home=${user_home} --state-dir=/var/lib/studyguard
Restart=always
RestartSec=2
NoNewPrivileges=false
ProtectHome=read-only
PrivateTmp=true
TimeoutStopSec=15

[Install]
WantedBy=multi-user.target
UNIT

install -d -o "${user_name}" -g "$(id -gn "${user_name}")" -m 0755 "${user_home}/.local/share/applications"
cat > "${user_home}/.local/share/applications/studyguard.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=StudyGuard
Comment=تنظيم وقتك وحجب البرامج والمواقع
Exec=${release_dir}/app/StudyGuard.Linux
Terminal=false
Categories=Utility;Education;
DESKTOP
chown "${user_name}:$(id -gn "${user_name}")" "${user_home}/.local/share/applications/studyguard.desktop"
chmod 0644 "${user_home}/.local/share/applications/studyguard.desktop"

systemctl daemon-reload
if [[ "${existing_service}" -eq 1 ]]; then
  systemctl enable studyguard-agent.service
  systemctl restart studyguard-agent.service
else
  systemctl enable --now studyguard-agent.service
fi
echo "تم تثبيت StudyGuard وتشغيل وكيل الحماية. افتح StudyGuard من قائمة التطبيقات."
