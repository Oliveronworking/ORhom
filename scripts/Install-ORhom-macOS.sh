#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
source_app="${repo_dir}/build/macos/ORhom.app"
install_dir="${1:-/Applications}"
destination_app="${install_dir}/ORhom.app"

"${script_dir}/Build-ORhom-macOS.sh"

if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  /usr/bin/osascript -e 'tell application id "at.orhom.mac" to quit' >/dev/null 2>&1 || true
  for _ in {1..120}; do
    if ! /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
      break
    fi
    /bin/sleep 0.25
  done
  if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
    echo "ORhom could not be closed cleanly; installation stopped." >&2
    exit 1
  fi
fi

/bin/mkdir -p "${install_dir}"
/usr/bin/ditto "${source_app}" "${destination_app}"
/usr/bin/codesign --verify --deep --strict "${destination_app}"
/usr/bin/open "${destination_app}"

echo "${destination_app}"
