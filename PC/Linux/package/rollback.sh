#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "شغّل الرجوع هكذا: sudo ./rollback.sh" >&2
  exit 1
fi
if [[ -z "${SUDO_USER:-}" || "${SUDO_USER}" == "root" ]]; then
  echo "شغّل الرجوع من حسابك باستخدام sudo، وليس من جلسة root مباشرة." >&2
  exit 1
fi

legacy_app=/opt/studyguard/app/StudyGuard.Linux
legacy_agent=/opt/studyguard/agent/StudyGuard.Agent
if [[ ! -x "${legacy_app}" || ! -x "${legacy_agent}" ]]; then
  echo "لم يُعثر على تثبيت StudyGuard السابق؛ لم يتم تغيير الخدمة الحالية." >&2
  exit 1
fi

user_name="${SUDO_USER}"
user_id="$(id -u "${user_name}")"
user_home="$(getent passwd "${user_name}" | cut -d: -f6)"
systemctl disable --now studyguard-agent.service
if [[ -e "/var/lib/studyguard/gnome-proxy-${user_id}.json" ]]; then
  echo "تعذرت استعادة إعداد وكيل GNOME. أُعيد تشغيل الخدمة الحالية ولم يتم الرجوع." >&2
  systemctl enable --now studyguard-agent.service
  exit 1
fi

cat > /etc/systemd/system/studyguard-agent.service <<UNIT
[Unit]
Description=StudyGuard Linux activity protection
After=local-fs.target

[Service]
Type=simple
ExecStart=${legacy_agent} --config=${user_home}/.config/studyguard/config.json --uid=${user_id}
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
Exec=${legacy_app}
Terminal=false
Categories=Utility;Education;
DESKTOP
chown "${user_name}:$(id -gn "${user_name}")" "${user_home}/.local/share/applications/studyguard.desktop"
chmod 0644 "${user_home}/.local/share/applications/studyguard.desktop"

systemctl daemon-reload
systemctl enable --now studyguard-agent.service
echo "تم الرجوع إلى التثبيت السابق. بقيت الإعدادات والملفات الشخصية دون تغيير."
