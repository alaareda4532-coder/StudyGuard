#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "شغّل الإزالة هكذا: sudo ./uninstall.sh" >&2
  exit 1
fi

systemctl disable --now studyguard-agent.service 2>/dev/null || true
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

rm -rf /opt/studyguard
if [[ -n "${SUDO_USER:-}" && "${SUDO_USER}" != "root" ]]; then
  home="$(getent passwd "${SUDO_USER}" | cut -d: -f6)"
  rm -f "${home}/.local/share/applications/studyguard.desktop"
fi
echo "تمت إزالة البرنامج ووكيل الحماية. بقيت إعداداتك الشخصية في ~/.config/studyguard."
