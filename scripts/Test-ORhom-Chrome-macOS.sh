#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
fixture_path="${repo_dir}/macos/tests/fixtures/contenteditable.html"
test_token="ORhomChromeTest$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
test_window_id=""
original_frontmost_pid="$(
  /usr/bin/osascript \
    -e 'tell application "System Events" to return unix id of first application process whose frontmost is true' \
    2>/dev/null || true
)"

restore_original_frontmost() {
  if [[ "${original_frontmost_pid}" != <-> ]]; then
    return
  fi
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
}

cleanup() {
  if [[ "${test_window_id}" == <-> ]]; then
    /usr/bin/osascript \
      -e 'tell application "Google Chrome"' \
      -e "if exists (first window whose id is ${test_window_id}) then close (first window whose id is ${test_window_id})" \
      -e 'end tell' >/dev/null 2>&1 || true
  fi
  /usr/bin/open "${app_path}" >/dev/null 2>&1 || true
  restore_original_frontmost
}
trap cleanup EXIT

if [[ ! -x "${app_binary}" ]]; then
  echo "Install ORhom.app before running the Chrome integration test." >&2
  exit 1
fi
if ! /usr/bin/pgrep -x "Google Chrome" >/dev/null 2>&1; then
  echo "Google Chrome must already be running for this integration test." >&2
  exit 1
fi

/usr/bin/osascript \
  -e 'tell application id "at.orhom.mac" to quit' >/dev/null 2>&1 || true
for _ in {1..80}; do
  if ! /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
    break
  fi
  /bin/sleep 0.05
done
if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  echo "ORhom could not be closed for the paste test." >&2
  exit 1
fi

test_window_id="$(
  /usr/bin/osascript \
    -e 'tell application "Google Chrome"' \
    -e 'set testWindow to make new window' \
    -e "set URL of active tab of testWindow to \"file://${fixture_path}\"" \
    -e 'activate' \
    -e 'return id of testWindow' \
    -e 'end tell'
)"
if [[ "${test_window_id}" != <-> ]]; then
  echo "Chrome did not return a valid test-window identifier." >&2
  exit 1
fi

for _ in {1..120}; do
  tab_title="$(
    /usr/bin/osascript \
      -e 'tell application "Google Chrome"' \
      -e "set testWindow to first window whose id is ${test_window_id}" \
      -e 'return title of active tab of testWindow' \
      -e 'end tell' \
      2>/dev/null || true
  )"
  if [[ "${tab_title}" == "ORHOM_TEST:READY" ]]; then
    break
  fi
  /bin/sleep 0.05
done
if [[ "${tab_title:-}" != "ORHOM_TEST:READY" ]]; then
  echo "Chrome did not finish loading the contenteditable fixture." >&2
  exit 1
fi
/bin/sleep 1

/usr/bin/open -g -W -a "${app_path}" \
  --env "ORHOM_DIAGNOSTIC_TEXT=${test_token}" \
  --env "ORHOM_DIAGNOSTIC_EXPECTED_BUNDLE=com.google.Chrome" \
  --env "ORHOM_DIAGNOSTIC_SIMULATE_INTERVENING_INPUT=1" \
  --env "ORHOM_DIAGNOSTIC_TRACE_FOCUS=1" \
  --args --diagnose-paste

tab_title="$(
  /usr/bin/osascript \
    -e 'tell application "Google Chrome"' \
    -e "set testWindow to first window whose id is ${test_window_id}" \
    -e 'return title of active tab of testWindow' \
    -e 'end tell'
)"
if [[ "${tab_title}" != "ORHOM_TEST:${test_token}" ]]; then
  echo "Chrome contenteditable did not receive the ORhom paste." >&2
  exit 1
fi

/usr/bin/open "${app_path}" >/dev/null 2>&1 || true
for _ in {1..80}; do
  if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
    echo "ORhom Chrome contenteditable integration test passed."
    exit 0
  fi
  /bin/sleep 0.05
done

echo "The Chrome integration test passed, but ORhom did not restart." >&2
exit 1
