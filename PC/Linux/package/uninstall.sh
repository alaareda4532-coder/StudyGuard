#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "شغّل الإزالة هكذا: sudo ./uninstall.sh" >&2
  exit 1
fi
if [[ -z "${SUDO_USER:-}" || "${SUDO_USER}" == "root" ]]; then
  echo "شغّل الإزالة من حسابك باستخدام sudo، وليس من جلسة root مباشرة." >&2
  exit 1
fi

if [[ -f /etc/systemd/system/studyguard-agent.service ]]; then
  systemctl disable --now studyguard-agent.service
fi

user_id="$(id -u "${SUDO_USER}")"
state_file="/var/lib/studyguard/gnome-proxy-${user_id}.json"
if [[ -e "${state_file}" ]]; then
  echo "تعذر استعادة إعدادات وكيل GNOME. لم تتم إزالة الملفات؛ سجّل الدخول إلى سطح المكتب وحاول مرة أخرى." >&2
  systemctl enable --now studyguard-agent.service
  exit 1
fi

rm -f /etc/systemd/system/studyguard-agent.service
systemctl daemon-reload

hosts=/etc/hosts
if [[ -f "${hosts}" ]]; then
  temp="$(mktemp /etc/hosts.studyguard.XXXXXX)"
  awk '
    /^# BEGIN STUDYGUARD MANAGED BLOCK$/ { skip=1; next }
    /^# END STUDYGUARD MANAGED BLOCK$/ { skip=0; next }
    !skip { print }
  ' "${hosts}" > "${temp}"
  chmod --reference="${hosts}" "${temp}"
  chown --reference="${hosts}" "${temp}"
  mv -f "${temp}" "${hosts}"
fi

rm -rf /opt/studyguard/releases/0.3.0
home="$(getent passwd "${SUDO_USER}" | cut -d: -f6)"
rm -f "${home}/.local/share/applications/studyguard.desktop"
echo "تمت إزالة البرنامج ووكيل الحماية. بقيت إعداداتك الشخصية في ~/.config/studyguard."
