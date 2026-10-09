#!/bin/zsh
set -uo pipefail

script_dir="${0:A:h}"
app_path="/Applications/ORhom.app"
original_frontmost_pid="$(
  /usr/bin/osascript \
    -e 'tell application "System Events" to return unix id of first application process whose frontmost is true' \
    2>/dev/null || true
)"
orhom_was_running=false
typeset -a result_labels
typeset -a result_states
failure_count=0

if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  orhom_was_running=true
fi

stop_orhom_best_effort() {
  if ! /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
    return
  fi
  /usr/bin/osascript \
    -e 'tell application id "at.orhom.mac" to quit' \
    >/dev/null 2>&1 || true
  for _ in {1..100}; do
    if ! /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
      return
    fi
    /bin/sleep 0.05
  done
}

restore_suite_state() {
  if [[ "${orhom_was_running}" == true ]]; then
    if ! /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
      /usr/bin/open "${app_path}" >/dev/null 2>&1 || true
    fi
  else
    stop_orhom_best_effort
  fi

  if [[ "${original_frontmost_pid}" == <-> ]]; then
    ORHOM_ORIGINAL_FRONTMOST_PID="${original_frontmost_pid}" \
      /usr/bin/osascript >/dev/null 2>&1 <<'APPLESCRIPT' || true
set originalPID to (system attribute "ORHOM_ORIGINAL_FRONTMOST_PID") as integer
tell application "System Events"
  set matches to every application process whose unix id is originalPID
  if (count matches) is 1 then
    set frontmost of item 1 of matches to true
  end if
end tell
APPLESCRIPT
  fi
}
trap restore_suite_state EXIT

run_test() {
  local label="$1"
  local test_script="$2"

  echo
  echo "=== ${label} ==="
  if [[ ! -x "${test_script}" ]]; then
    echo "FAIL ${label}: script is missing or not executable." >&2
    result_labels+=("${label}")
    result_states+=("FAIL")
    (( failure_count += 1 ))
    return
  fi

  if "${test_script}"; then
    result_labels+=("${label}")
    result_states+=("PASS")
  else
    result_labels+=("${label}")
    result_states+=("FAIL")
    (( failure_count += 1 ))
  fi
}

echo "ORhom text-field matrix runs serially because macOS focus and the clipboard are global."

run_test \
  "macOS policy/unit tests" \
  "${script_dir}/Test-ORhom-macOS.sh"
run_test \
  "Native AppKit and SwiftUI fields" \
  "${script_dir}/Test-ORhom-NativeTextFields-macOS.sh"
run_test \
  "TextEdit native editor" \
  "${script_dir}/Test-ORhom-TextEdit-macOS.sh"
run_test \
  "Chrome isolated web fields" \
  "${script_dir}/Test-ORhom-ChromeTextFields-macOS.sh"
run_test \
  "Safari isolated web fields" \
  "${script_dir}/Test-ORhom-SafariTextFields-macOS.sh"
run_test \
  "VS Code isolated editor" \
  "${script_dir}/Test-ORhom-VSCode-macOS.sh"
run_test \
  "ChatGPT Quick Chat" \
  "${script_dir}/Test-ORhom-ChatGPT-macOS.sh"

echo
echo "=== ORhom text-field matrix summary ==="
for (( index = 1; index <= ${#result_labels[@]}; index++ )); do
  echo "${result_states[index]} ${result_labels[index]}"
done

if (( failure_count > 0 )); then
  echo "${failure_count} text-field matrix group(s) failed." >&2
  exit 1
fi

echo "All ORhom text-field matrix groups passed."
