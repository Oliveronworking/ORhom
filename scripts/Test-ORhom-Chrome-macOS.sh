#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
log_path="${HOME}/Library/Logs/ORhom/app.log"
fixture_path="${repo_dir}/macos/tests/fixtures/contenteditable.html"
run_id="$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
clipboard_sentinel="ORhomChromeClipboard${run_id}"
test_temp_dir="$(/usr/bin/mktemp -d -t orhom-chrome-fields)"
clipboard_before_path="${test_temp_dir}/clipboard-before.json"
clipboard_after_path="${test_temp_dir}/clipboard-after.json"
test_window_id=""
current_test_token=""
orhom_was_running=false
clipboard_snapshot_captured=false
clipboard_restored=false
clipboard_restore_outcome=""
original_frontmost_pid="$(
  /usr/bin/osascript \
    -e 'tell application "System Events" to return unix id of first application process whose frontmost is true' \
    2>/dev/null || true
)"

snapshot_clipboard() {
  local output_path="$1"
  /usr/bin/osascript -l JavaScript - >"${output_path}" <<'JXA'
ObjC.import("AppKit");

function run() {
  const pasteboard = $.NSPasteboard.generalPasteboard;
  const snapshot = [];
  const items = pasteboard.pasteboardItems;
  if (items) {
    for (let itemIndex = 0; itemIndex < items.count; itemIndex++) {
      const item = items.objectAtIndex(itemIndex);
      const entries = [];
      const types = item.types;
      for (let typeIndex = 0; typeIndex < types.count; typeIndex++) {
        const type = types.objectAtIndex(typeIndex);
        const data = item.dataForType(type);
        if (!data) {
          throw new Error(
            "Clipboard type could not be snapshotted: " +
              ObjC.unwrap(type)
          );
        }
        entries.push({
          type: ObjC.unwrap(type),
          data: ObjC.unwrap(
            data.base64EncodedStringWithOptions(0)
          )
        });
      }
      entries.sort((left, right) =>
        left.type.localeCompare(right.type)
      );
      snapshot.push(entries);
    }
  }
  return JSON.stringify(snapshot);
}
JXA
}

set_clipboard_sentinel() {
  ORHOM_TEST_CLIPBOARD_SENTINEL="${clipboard_sentinel}" \
    /usr/bin/osascript -l JavaScript - >/dev/null <<'JXA'
ObjC.import("AppKit");

function run() {
  const sentinel = ObjC.unwrap(
    $.NSProcessInfo.processInfo.environment.objectForKey(
      "ORHOM_TEST_CLIPBOARD_SENTINEL"
    )
  );
  const pasteboard = $.NSPasteboard.generalPasteboard;
  pasteboard.clearContents;
  if (!pasteboard.setStringForType($(sentinel), $.NSPasteboardTypeString)) {
    throw new Error("Clipboard sentinel could not be written.");
  }
}
JXA
}

restore_clipboard_if_owned() {
  local outcome
  if [[ "${clipboard_snapshot_captured}" != true ]]; then
    return 0
  fi
  if ! outcome="$(
    /usr/bin/osascript -l JavaScript - \
      "${clipboard_before_path}" "${clipboard_sentinel}" \
      "${current_test_token}" <<'JXA'
ObjC.import("AppKit");
ObjC.import("Foundation");

function run(argv) {
  const snapshotPath = argv[0];
  const sentinel = argv[1];
  const activeToken = argv[2];
  const pasteboard = $.NSPasteboard.generalPasteboard;
  const currentString = pasteboard.stringForType(
    $.NSPasteboardTypeString
  );
  if (!currentString) {
    return "skipped";
  }
  const current = ObjC.unwrap(currentString);
  if (
    current !== sentinel &&
    (!activeToken || current !== activeToken)
  ) {
    return "skipped";
  }

  const snapshotText =
    $.NSString.stringWithContentsOfFileEncodingError(
      $(snapshotPath),
      $.NSUTF8StringEncoding,
      null
    );
  if (!snapshotText) {
    throw new Error("Clipboard snapshot could not be read.");
  }
  const snapshot = JSON.parse(ObjC.unwrap(snapshotText));
  const restoredItems = $.NSMutableArray.array;
  snapshot.forEach((entries) => {
    const item = $.NSPasteboardItem.alloc.init;
    entries.forEach((entry) => {
      const data =
        $.NSData.alloc.initWithBase64EncodedStringOptions(
          $(entry.data),
          0
        );
      if (!data || !item.setDataForType(data, $(entry.type))) {
        throw new Error(
          "Clipboard type could not be restored: " + entry.type
        );
      }
    });
    restoredItems.addObject(item);
  });

  pasteboard.clearContents;
  if (
    restoredItems.count > 0 &&
    !pasteboard.writeObjects(restoredItems)
  ) {
    throw new Error("Clipboard snapshot could not be restored.");
  }
  return "restored";
}
JXA
  )"; then
    return 1
  fi
  clipboard_restore_outcome="${outcome}"
}

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

restore_orhom_state() {
  stop_orhom_best_effort
  if [[ "${orhom_was_running}" == true ]]; then
    /usr/bin/open "${app_path}" >/dev/null 2>&1 || true
  fi
}

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

close_test_window_if_owned() {
  if [[ "${test_window_id}" != <-> ]]; then
    return
  fi
  ORHOM_CHROME_TEST_WINDOW_ID="${test_window_id}" \
  ORHOM_CHROME_FIXTURE_PATH="${fixture_path}" \
    /usr/bin/osascript >/dev/null 2>&1 <<'APPLESCRIPT' || true
set testWindowID to (system attribute "ORHOM_CHROME_TEST_WINDOW_ID") as integer
set fixturePath to system attribute "ORHOM_CHROME_FIXTURE_PATH"
tell application "Google Chrome"
  set matches to every window whose id is testWindowID
  if (count matches) is 1 then
    set candidateWindow to item 1 of matches
    set candidateURL to ""
    set candidateTitle to ""
    try
      set candidateURL to URL of active tab of candidateWindow
      set candidateTitle to title of active tab of candidateWindow
    end try
    if candidateURL starts with ("file://" & fixturePath) or candidateTitle starts with "ORHOM_TEST:" then
      close candidateWindow
    end if
  end if

  set staleFixtureWindowIDs to {}
  repeat with candidateWindow in every window
    set candidateURL to ""
    set candidateTitle to ""
    try
      set candidateURL to URL of active tab of candidateWindow
      set candidateTitle to title of active tab of candidateWindow
    end try
    if candidateURL starts with ("file://" & fixturePath) or candidateTitle starts with "ORHOM_TEST:" then
      set end of staleFixtureWindowIDs to id of candidateWindow
    end if
  end repeat
  repeat with staleWindowID in staleFixtureWindowIDs
    try
      close window id (staleWindowID as integer)
    end try
  end repeat
end tell
APPLESCRIPT
  test_window_id=""
}

cleanup() {
  close_test_window_if_owned
  restore_orhom_state
  if [[ "${clipboard_restored}" != true ]]; then
    if ! restore_clipboard_if_owned; then
      echo "Warning: the original clipboard could not be recovered." >&2
    elif [[ "${clipboard_restore_outcome}" == "skipped" ]]; then
      echo "The clipboard changed externally; the test preserved that newer content." >&2
    fi
  fi
  restore_original_frontmost
  /bin/rm -rf -- "${test_temp_dir}"
}
trap cleanup EXIT

new_log_since() {
  local start_size="$1"
  local current_size
  if [[ ! -f "${log_path}" ]]; then
    return
  fi
  current_size="$(/usr/bin/stat -f '%z' "${log_path}")"
  if (( current_size < start_size )); then
    start_size=0
  fi
  /usr/bin/tail -c "+$((start_size + 1))" "${log_path}"
}

set_fixture_case() {
  local case_name="$1"
  local fixture_url="file://${fixture_path}?case=${case_name}&run=${run_id}"
  ORHOM_CHROME_TEST_WINDOW_ID="${test_window_id}" \
  ORHOM_CHROME_FIXTURE_URL="${fixture_url}" \
    /usr/bin/osascript >/dev/null <<'APPLESCRIPT'
set testWindowID to (system attribute "ORHOM_CHROME_TEST_WINDOW_ID") as integer
set fixtureURL to system attribute "ORHOM_CHROME_FIXTURE_URL"
tell application "Google Chrome"
  set testWindow to first window whose id is testWindowID
  set URL of active tab of testWindow to fixtureURL
  set index of testWindow to 1
  activate
end tell
APPLESCRIPT
}

active_tab_title() {
  ORHOM_CHROME_TEST_WINDOW_ID="${test_window_id}" \
    /usr/bin/osascript <<'APPLESCRIPT'
set testWindowID to (system attribute "ORHOM_CHROME_TEST_WINDOW_ID") as integer
tell application "Google Chrome"
  return title of active tab of (first window whose id is testWindowID)
end tell
APPLESCRIPT
}

active_tab_url() {
  ORHOM_CHROME_TEST_WINDOW_ID="${test_window_id}" \
    /usr/bin/osascript <<'APPLESCRIPT'
set testWindowID to (system attribute "ORHOM_CHROME_TEST_WINDOW_ID") as integer
tell application "Google Chrome"
  return URL of active tab of (first window whose id is testWindowID)
end tell
APPLESCRIPT
}

wait_for_title() {
  local expected_title="$1"
  local observed_title=""
  for _ in {1..160}; do
    observed_title="$(active_tab_title 2>/dev/null || true)"
    if [[ "${observed_title}" == "${expected_title}" ]]; then
      return 0
    fi
    /bin/sleep 0.05
  done
  echo "Expected Chrome title '${expected_title}', observed '${observed_title}'." >&2
  return 1
}

focused_chrome_identity() {
  /usr/bin/osascript <<'APPLESCRIPT'
tell application "System Events"
  tell application process "Google Chrome"
    if frontmost is not true then
      error "Chrome is not frontmost."
    end if
    set focusedElement to value of attribute "AXFocusedUIElement"
    set focusedRole to role of focusedElement as text
    set focusedSubrole to ""
    try
      set focusedSubrole to subrole of focusedElement as text
    end try
    return focusedRole & "|" & focusedSubrole
  end tell
end tell
APPLESCRIPT
}

run_web_case() {
  local case_name="$1"
  local expect_secure="$2"
  local test_token="ORhomChrome${case_name}${run_id}"
  local expected_title="ORHOM_TEST:VALUE:${case_name}:LEFT${test_token}RIGHT"
  local log_size_before=0
  local focused_identity
  local new_log

  current_test_token="${test_token}"
  set_fixture_case "${case_name}"
  wait_for_title "ORHOM_TEST:READY:${case_name}"
  /bin/sleep 0.12
  focused_identity="$(focused_chrome_identity)"

  if [[ "${expect_secure}" == true ]]; then
    if [[ "${focused_identity}" != *"|AXSecureTextField" ]]; then
      echo "Chrome password input is not exposed as AXSecureTextField: ${focused_identity}" >&2
      return 1
    fi
  elif [[ "${focused_identity}" != AXTextField* &&
          "${focused_identity}" != AXTextArea* &&
          "${focused_identity}" != AXSearchField* ]]; then
    echo "Chrome ${case_name} does not expose an editable AX role: ${focused_identity}" >&2
    return 1
  fi

  if [[ -f "${log_path}" ]]; then
    log_size_before="$(/usr/bin/stat -f '%z' "${log_path}")"
  fi
  /usr/bin/open -g -W -a "${app_path}" \
    --env "ORHOM_DIAGNOSTIC_TEXT=${test_token}" \
    --env "ORHOM_DIAGNOSTIC_EXPECTED_BUNDLE=com.google.Chrome" \
    --env "ORHOM_DIAGNOSTIC_SIMULATE_INTERVENING_INPUT=1" \
    --env "ORHOM_DIAGNOSTIC_TRACE_FOCUS=1" \
    --args --diagnose-paste
  new_log="$(new_log_since "${log_size_before}")"

  if [[ "${expect_secure}" == true ]]; then
    wait_for_title "ORHOM_TEST:READY:password"
    if [[ "${new_log}" != *"Paste fallback. Reason='secure-text-field'"* &&
          "${new_log}" != *"unsafe-target secure-text-field"* ]]; then
      echo "ORhom did not log rejection of the Chrome password input." >&2
      return 1
    fi
    if [[ "${new_log}" == *"Paste dispatched."* ||
          "${new_log}" == *"Paste completed. Method=AXSelectedText"* ]]; then
      echo "ORhom attempted insertion into the Chrome password input." >&2
      return 1
    fi
    echo "PASS Chrome password (${focused_identity}, blocked-secure)"
  else
    wait_for_title "${expected_title}"
    if [[ "${new_log}" == *"Paste fallback"* ||
          "${new_log}" == *"Paste diagnostic failed"* ||
          "${new_log}" == *"Paste failed"* ||
          "${new_log}" == *"Verified=false"* ]]; then
      echo "ORhom logged a fallback or failed verification for Chrome ${case_name}." >&2
      return 1
    fi
    if [[ "${new_log}" != *"Paste dispatch completed."* ||
          "${new_log}" != *"Verified=true"* ||
          "${new_log}" != *"ClipboardRestored=true"* ]]; then
      echo "ORhom did not confirm the Chrome ${case_name} keyboard paste." >&2
      return 1
    fi
    echo "PASS Chrome ${case_name} (${focused_identity}, verified keyboard paste)"
  fi

  if [[ "${new_log}" != *"Paste diagnostic finished. Settled=true."* ]]; then
    echo "The Chrome ${case_name} diagnostic did not settle." >&2
    return 1
  fi
  if [[ "$(
    /usr/bin/pbpaste 2>/dev/null || true
  )" != "${clipboard_sentinel}" ]]; then
    echo "The clipboard changed during Chrome ${case_name}; newer clipboard content was preserved." >&2
    return 1
  fi
  current_test_token=""
}

run_omnibox_case() {
  local test_token="ORhomChromeOmnibox${run_id}"
  local fixture_url
  local url_after
  local focused_identity
  local focused_value
  local log_size_before=0
  local new_log

  current_test_token="${test_token}"
  set_fixture_case contenteditable
  wait_for_title "ORHOM_TEST:READY:contenteditable"
  fixture_url="$(active_tab_url)"

  /usr/bin/osascript >/dev/null <<'APPLESCRIPT'
tell application "System Events"
  tell application process "Google Chrome"
    set frontmost to true
    keystroke "l" using command down
  end tell
end tell
APPLESCRIPT
  /bin/sleep 0.15
  focused_identity="$(focused_chrome_identity)"
  if [[ "${focused_identity}" != AXTextField* &&
        "${focused_identity}" != AXSearchField* ]]; then
    echo "Chrome omnibox does not expose an AX text/search field: ${focused_identity}" >&2
    return 1
  fi

  if [[ -f "${log_path}" ]]; then
    log_size_before="$(/usr/bin/stat -f '%z' "${log_path}")"
  fi
  /usr/bin/open -g -W -a "${app_path}" \
    --env "ORHOM_DIAGNOSTIC_TEXT=${test_token}" \
    --env "ORHOM_DIAGNOSTIC_EXPECTED_BUNDLE=com.google.Chrome" \
    --env "ORHOM_DIAGNOSTIC_SIMULATE_INTERVENING_INPUT=1" \
    --env "ORHOM_DIAGNOSTIC_TRACE_FOCUS=1" \
    --args --diagnose-paste
  new_log="$(new_log_since "${log_size_before}")"

  focused_value="$(
    /usr/bin/osascript <<'APPLESCRIPT'
tell application "System Events"
  tell application process "Google Chrome"
    set focusedElement to value of attribute "AXFocusedUIElement"
    return value of focusedElement as text
  end tell
end tell
APPLESCRIPT
  )"
  url_after="$(active_tab_url)"

  if [[ "${focused_value}" != "${test_token}" ]]; then
    echo "The Chrome omnibox did not contain exactly the diagnostic token." >&2
    return 1
  fi
  if [[ "${url_after}" != "${fixture_url}" ]]; then
    echo "The Chrome omnibox test unexpectedly navigated the test tab." >&2
    return 1
  fi
  if [[ "${new_log}" != *"Paste completed. Method=AXSelectedText"* ||
        "${new_log}" != *"PreserveWebViewCaret=false"* ]]; then
    echo "ORhom did not use direct AX insertion for the Chrome omnibox." >&2
    return 1
  fi
  if [[ "${new_log}" == *"Paste fallback"* ||
        "${new_log}" == *"Paste diagnostic failed"* ||
        "${new_log}" == *"Paste failed"* ]]; then
    echo "ORhom logged an omnibox paste failure." >&2
    return 1
  fi
  if [[ "${new_log}" != *"Paste diagnostic finished. Settled=true."* ]]; then
    echo "The Chrome omnibox diagnostic did not settle." >&2
    return 1
  fi
  if [[ "$(
    /usr/bin/pbpaste 2>/dev/null || true
  )" != "${clipboard_sentinel}" ]]; then
    echo "The clipboard changed during the Chrome omnibox test." >&2
    return 1
  fi

  current_test_token=""
  echo "PASS Chrome omnibox (${focused_identity}, AXSelectedText, no navigation)"
}

if [[ ! -x "${app_binary}" ]]; then
  echo "Install the current ORhom.app before running the Chrome field tests." >&2
  exit 1
fi
if [[ ! -f "${fixture_path}" ]]; then
  echo "The Chrome field fixture is missing." >&2
  exit 1
fi
if ! /usr/bin/pgrep -x "Google Chrome" >/dev/null 2>&1; then
  echo "Google Chrome must already be running; the test will not launch or quit the user's browser." >&2
  exit 1
fi
if [[ "${original_frontmost_pid}" != <-> ]]; then
  echo "The originally frontmost application could not be identified." >&2
  exit 1
fi

if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  orhom_was_running=true
fi
stop_orhom_best_effort
if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  echo "ORhom could not be stopped for the Chrome field tests." >&2
  exit 1
fi

snapshot_clipboard "${clipboard_before_path}"
clipboard_snapshot_captured=true
set_clipboard_sentinel

test_window_id="$(
  ORHOM_CHROME_FIXTURE_URL="file://${fixture_path}?case=contenteditable&run=${run_id}" \
    /usr/bin/osascript <<'APPLESCRIPT'
set fixtureURL to system attribute "ORHOM_CHROME_FIXTURE_URL"
tell application "Google Chrome"
  set testWindow to make new window
  set URL of active tab of testWindow to fixtureURL
  set index of testWindow to 1
  activate
  return id of testWindow
end tell
APPLESCRIPT
)"
if [[ "${test_window_id}" != <-> ]]; then
  echo "Chrome did not return a valid test-window identifier." >&2
  exit 1
fi

if [[ "${ORHOM_CHROME_TEST_SELECTION:-all}" != "omnibox" ]]; then
  run_web_case text false
  run_web_case textarea false
  run_web_case search false
  run_web_case contenteditable false
  run_web_case password true
fi
if [[ "${ORHOM_CHROME_TEST_SELECTION:-all}" != "web" ]]; then
  run_omnibox_case
fi

close_test_window_if_owned

if ! restore_clipboard_if_owned; then
  echo "The original clipboard could not be restored after the Chrome field tests." >&2
  exit 1
fi
if [[ "${clipboard_restore_outcome}" != "restored" ]]; then
  echo "The clipboard changed externally; its newer contents were preserved." >&2
  exit 1
fi
clipboard_restored=true
snapshot_clipboard "${clipboard_after_path}"
if ! /usr/bin/cmp -s \
  "${clipboard_before_path}" "${clipboard_after_path}"; then
  echo "The Chrome field tests did not restore the clipboard exactly." >&2
  exit 1
fi

echo "ORhom Chrome field matrix passed (text, textarea, search, contenteditable, password negative, omnibox)."
