#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
fixture_source="${repo_dir}/macos/tests/fixtures/ORhomTextFieldsFixture.swift"
app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
log_path="${HOME}/Library/Logs/ORhom/app.log"
run_id="$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
clipboard_sentinel="ORhomAppKitClipboard${run_id}"
test_temp_dir="$(/usr/bin/mktemp -d -t orhom-appkit-fields)"
fixture_app="${test_temp_dir}/ORhomTextFieldsFixture.app"
fixture_binary="${fixture_app}/Contents/MacOS/ORhomTextFieldsFixture"
clipboard_before_path="${test_temp_dir}/clipboard-before.json"
clipboard_after_path="${test_temp_dir}/clipboard-after.json"
original_frontmost_pid="$(
  /usr/bin/osascript \
    -e 'tell application "System Events" to return unix id of first application process whose frontmost is true' \
    2>/dev/null || true
)"
orhom_was_running=false
clipboard_snapshot_captured=false
clipboard_restored=false
clipboard_restore_outcome=""
fixture_pid=""
current_test_token=""

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

stop_fixture_best_effort() {
  if [[ "${fixture_pid}" != <-> ]]; then
    fixture_pid=""
    return
  fi
  local command_text
  command_text="$(
    /bin/ps -p "${fixture_pid}" -o command= 2>/dev/null || true
  )"
  if [[ "${command_text}" == *"${fixture_binary}"* ]]; then
    /bin/kill -TERM "${fixture_pid}" >/dev/null 2>&1 || true
    for _ in {1..60}; do
      if ! /bin/kill -0 "${fixture_pid}" >/dev/null 2>&1; then
        break
      fi
      /bin/sleep 0.05
    done
  fi
  fixture_pid=""
}

cleanup() {
  stop_fixture_best_effort
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

json_value() {
  local path="$1"
  local key="$2"
  /usr/bin/plutil -extract "${key}" raw -o - "${path}" 2>/dev/null
}

wait_for_fixture_ready() {
  local result_path="$1"
  for _ in {1..120}; do
    if [[ -f "${result_path}" ]] &&
       [[ "$(json_value "${result_path}" ready || true)" == "true" ]]; then
      return 0
    fi
    /bin/sleep 0.05
  done
  return 1
}

focused_accessibility_identity() {
  local process_id="$1"
  ORHOM_FIXTURE_PID="${process_id}" \
    /usr/bin/osascript <<'APPLESCRIPT'
set fixturePID to (system attribute "ORHOM_FIXTURE_PID") as integer
tell application "System Events"
  set matches to every application process whose unix id is fixturePID
  if (count matches) is not 1 then
    error "The AppKit fixture process could not be identified."
  end if
  set fixtureProcess to item 1 of matches
  set frontmost of fixtureProcess to true
  delay 0.12
  set focusedElement to value of attribute "AXFocusedUIElement" of fixtureProcess
  set focusedRole to role of focusedElement as text
  set focusedSubrole to ""
  try
    set focusedSubrole to subrole of focusedElement as text
  end try
  return focusedRole & "|" & focusedSubrole
end tell
APPLESCRIPT
}

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

run_case() {
  local case_name="$1"
  local expect_secure="$2"
  local result_path="${test_temp_dir}/result-${case_name}.json"
  local test_token="ORhomAppKit${case_name}${run_id}"
  local expected_value="LEFT${test_token}RIGHT"
  local log_size_before=0
  local focused_identity
  local result_value
  local new_log
  local method

  current_test_token="${test_token}"
  if [[ -f "${log_path}" ]]; then
    log_size_before="$(/usr/bin/stat -f '%z' "${log_path}")"
  fi

  /usr/bin/open -n "${fixture_app}" --args \
    --case "${case_name}" \
    --result "${result_path}" \
    --run-id "${run_id}"

  if ! wait_for_fixture_ready "${result_path}"; then
    echo "The AppKit ${case_name} fixture did not become ready." >&2
    return 1
  fi
  fixture_pid="$(json_value "${result_path}" pid)"
  if [[ "${fixture_pid}" != <-> ]]; then
    echo "The AppKit ${case_name} fixture returned an invalid PID." >&2
    return 1
  fi
  if [[ "$(json_value "${result_path}" value)" != "LEFTRIGHT" ]]; then
    echo "The AppKit ${case_name} fixture has an unexpected initial value." >&2
    return 1
  fi

  focused_identity="$(focused_accessibility_identity "${fixture_pid}")"
  if [[ "${expect_secure}" == true ]]; then
    if [[ "${focused_identity}" != *"|AXSecureTextField" ]]; then
      echo "The secure fixture is not exposed as AXSecureTextField: ${focused_identity}" >&2
      return 1
    fi
  elif [[ "${focused_identity}" != AXTextField* &&
          "${focused_identity}" != AXTextArea* &&
          "${focused_identity}" != AXComboBox* &&
          "${focused_identity}" != AXSearchField* ]]; then
    echo "The ${case_name} fixture does not expose an editable AX role: ${focused_identity}" >&2
    return 1
  fi

  ORHOM_DIAGNOSTIC_TEXT="${test_token}" \
  ORHOM_DIAGNOSTIC_EXPECTED_BUNDLE="at.orhom.testfields" \
  ORHOM_DIAGNOSTIC_SIMULATE_INTERVENING_INPUT=1 \
    "${app_binary}" --diagnose-paste

  /bin/sleep 0.12
  result_value="$(json_value "${result_path}" value)"
  new_log="$(new_log_since "${log_size_before}")"

  if [[ "${expect_secure}" == true ]]; then
    if [[ "${result_value}" != "LEFTRIGHT" ]]; then
      echo "The secure AppKit field was modified." >&2
      return 1
    fi
    if [[ "${new_log}" != *"Paste fallback. Reason='secure-text-field'"* &&
          "${new_log}" != *"unsafe-target secure-text-field"* ]]; then
      echo "ORhom did not log a secure-field rejection for AppKit." >&2
      return 1
    fi
    if [[ "${new_log}" == *"Paste dispatched."* ||
          "${new_log}" == *"Paste completed. Method=AXSelectedText"* ]]; then
      echo "ORhom attempted insertion into the secure AppKit field." >&2
      return 1
    fi
    method="blocked-secure"
  else
    if [[ "${result_value}" != "${expected_value}" ]]; then
      echo "The AppKit ${case_name} field did not receive the token at its prepared caret." >&2
      echo "Expected: ${expected_value}" >&2
      echo "Actual:   ${result_value}" >&2
      return 1
    fi
    if [[ "${new_log}" == *"Paste fallback"* ||
          "${new_log}" == *"Paste diagnostic failed"* ||
          "${new_log}" == *"Paste failed"* ||
          "${new_log}" == *"Verified=false"* ]]; then
      echo "ORhom logged a fallback or failed verification for AppKit ${case_name}." >&2
      return 1
    fi
    if [[ "${new_log}" == *"Paste completed. Method=AXSelectedText"* ]]; then
      method="AXSelectedText"
    elif [[ "${new_log}" == *"Paste dispatch completed."* ]]; then
      method="keyboard"
    else
      echo "ORhom did not log a completed insertion for AppKit ${case_name}." >&2
      return 1
    fi
  fi

  if [[ "${new_log}" != *"Paste diagnostic finished. Settled=true."* ]]; then
    echo "The AppKit ${case_name} paste diagnostic did not settle." >&2
    return 1
  fi
  if [[ "$(
    /usr/bin/pbpaste 2>/dev/null || true
  )" != "${clipboard_sentinel}" ]]; then
    echo "The clipboard changed during AppKit ${case_name}; newer clipboard content was preserved." >&2
    return 1
  fi

  stop_fixture_best_effort
  current_test_token=""
  echo "PASS AppKit ${case_name} (${focused_identity}, ${method})"
}

if [[ ! -x "${app_binary}" ]]; then
  echo "Install the current ORhom.app before running the AppKit field tests." >&2
  exit 1
fi
if [[ ! -f "${fixture_source}" ]]; then
  echo "The AppKit text-field fixture source is missing." >&2
  exit 1
fi

if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  orhom_was_running=true
fi
stop_orhom_best_effort
if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  echo "ORhom could not be stopped for the AppKit field tests." >&2
  exit 1
fi

snapshot_clipboard "${clipboard_before_path}"
clipboard_snapshot_captured=true
set_clipboard_sentinel

/bin/mkdir -p "${fixture_app}/Contents/MacOS"
/usr/bin/plutil -create xml1 "${fixture_app}/Contents/Info.plist"
/usr/bin/plutil -insert CFBundleDevelopmentRegion -string en \
  "${fixture_app}/Contents/Info.plist"
/usr/bin/plutil -insert CFBundleExecutable \
  -string ORhomTextFieldsFixture "${fixture_app}/Contents/Info.plist"
/usr/bin/plutil -insert CFBundleIdentifier \
  -string at.orhom.testfields "${fixture_app}/Contents/Info.plist"
/usr/bin/plutil -insert CFBundleName \
  -string ORhomTextFieldsFixture "${fixture_app}/Contents/Info.plist"
/usr/bin/plutil -insert CFBundlePackageType \
  -string APPL "${fixture_app}/Contents/Info.plist"
/usr/bin/plutil -insert CFBundleShortVersionString \
  -string 1.0 "${fixture_app}/Contents/Info.plist"
/usr/bin/plutil -insert CFBundleVersion \
  -string 1 "${fixture_app}/Contents/Info.plist"
/usr/bin/plutil -insert NSHighResolutionCapable \
  -bool true "${fixture_app}/Contents/Info.plist"

macos_sdk="$(/usr/bin/xcrun --sdk macosx --show-sdk-path)"
module_cache="${test_temp_dir}/swift-module-cache"
/bin/mkdir -p "${module_cache}"

/usr/bin/xcrun swiftc \
  -swift-version 5 \
  -warnings-as-errors \
  -module-cache-path "${module_cache}" \
  -sdk "${macos_sdk}" \
  -target arm64-apple-macosx13.3 \
  "${fixture_source}" \
  -framework AppKit \
  -o "${fixture_binary}"

run_case textfield false
run_case textarea false
run_case search false
run_case combo false
run_case secure true

if ! restore_clipboard_if_owned; then
  echo "The original clipboard could not be restored after the AppKit field tests." >&2
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
  echo "The AppKit field tests did not restore the clipboard exactly." >&2
  exit 1
fi

echo "ORhom AppKit field matrix passed (text field, text area, search, combo, secure negative)."
