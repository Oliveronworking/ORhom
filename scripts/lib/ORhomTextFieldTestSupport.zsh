#!/bin/zsh

# Shared helpers for destructive-in-appearance, isolated macOS paste tests.
# Callers must set orhom_test_run_prefix before using clipboard recovery.

orhom_test_capture_frontmost_pid() {
  /usr/bin/osascript \
    -e 'tell application "System Events" to return unix id of first application process whose frontmost is true' \
    2>/dev/null || true
}

orhom_test_restore_frontmost_pid() {
  local process_identifier="$1"
  if [[ "${process_identifier}" != <-> ]]; then
    return
  fi
  ORHOM_TEST_ORIGINAL_FRONTMOST_PID="${process_identifier}" \
    /usr/bin/osascript >/dev/null 2>&1 <<'APPLESCRIPT' || true
set originalPID to (system attribute "ORHOM_TEST_ORIGINAL_FRONTMOST_PID") as integer
tell application "System Events"
  set matches to every application process whose unix id is originalPID
  if (count matches) is 1 then
    set frontmost of item 1 of matches to true
  end if
end tell
APPLESCRIPT
}

orhom_test_snapshot_clipboard() {
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

orhom_test_set_clipboard_text() {
  local value="$1"
  ORHOM_TEST_CLIPBOARD_VALUE="${value}" \
    /usr/bin/osascript >/dev/null <<'APPLESCRIPT'
set the clipboard to system attribute "ORHOM_TEST_CLIPBOARD_VALUE"
APPLESCRIPT
}

orhom_test_clipboard_text() {
  /usr/bin/osascript -l JavaScript - <<'JXA'
ObjC.import("AppKit");

function run() {
  const value = $.NSPasteboard.generalPasteboard.stringForType(
    $.NSPasteboardTypeString
  );
  return value ? ObjC.unwrap(value) : "";
}
JXA
}

orhom_test_restore_clipboard_if_owned() {
  local snapshot_path="$1"
  local ownership_prefix="$2"
  /usr/bin/osascript -l JavaScript - \
    "${snapshot_path}" "${ownership_prefix}" <<'JXA'
ObjC.import("AppKit");
ObjC.import("Foundation");

function run(argv) {
  const snapshotPath = argv[0];
  const ownershipPrefix = argv[1];
  const pasteboard = $.NSPasteboard.generalPasteboard;
  const currentString = pasteboard.stringForType(
    $.NSPasteboardTypeString
  );
  if (
    !currentString ||
    !ObjC.unwrap(currentString).startsWith(ownershipPrefix)
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
          "Clipboard type could not be restored: " +
            entry.type
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
}

orhom_test_stop_orhom() {
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
  echo "ORhom could not be closed for the text-field matrix." >&2
  return 1
}

orhom_test_restart_orhom() {
  local app_path="$1"
  /usr/bin/open "${app_path}" >/dev/null 2>&1 || return 1
  for _ in {1..100}; do
    if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
      return
    fi
    /bin/sleep 0.05
  done
  echo "ORhom did not restart after the text-field matrix." >&2
  return 1
}

orhom_test_log_offset() {
  local log_path="$1"
  if [[ -f "${log_path}" ]]; then
    /usr/bin/stat -f '%z' "${log_path}"
  else
    echo 0
  fi
}

orhom_test_write_log_segment() {
  local log_path="$1"
  local starting_offset="$2"
  local output_path="$3"
  local ending_offset=0
  if [[ -f "${log_path}" ]]; then
    ending_offset="$(/usr/bin/stat -f '%z' "${log_path}")"
  fi
  if (( ending_offset < starting_offset )); then
    starting_offset=0
  fi
  if [[ -f "${log_path}" ]]; then
    /usr/bin/tail -c "+$((starting_offset + 1))" \
      "${log_path}" >"${output_path}"
  else
    : >"${output_path}"
  fi
}

orhom_test_run_diagnostic() {
  local app_path="$1"
  local text="$2"
  local expected_bundle="$3"
  local expected_pid="${4:-}"
  local expected_window_number="${5:-}"
  local simulate_intervening_input="${6:-1}"
  /usr/bin/open -g -W -a "${app_path}" \
    --env "ORHOM_DIAGNOSTIC_TEXT=${text}" \
    --env "ORHOM_DIAGNOSTIC_EXPECTED_BUNDLE=${expected_bundle}" \
    --env "ORHOM_DIAGNOSTIC_EXPECTED_PID=${expected_pid}" \
    --env "ORHOM_DIAGNOSTIC_EXPECTED_WINDOW_NUMBER=${expected_window_number}" \
    --env "ORHOM_DIAGNOSTIC_SIMULATE_INTERVENING_INPUT=${simulate_intervening_input}" \
    --args --diagnose-paste
}

orhom_test_assert_positive_log() {
  local log_segment_path="$1"
  local expected_bundle="$2"
  local expected_pid="$3"
  if ! /usr/bin/grep -F "Focus captured." "${log_segment_path}" |
    /usr/bin/grep -Fq \
      "TargetPID=${expected_pid} Bundle='${expected_bundle}'"; then
    echo "ORhom did not capture the expected application bundle." >&2
    return 1
  fi
  if ! /usr/bin/grep -Eq \
    "Paste completed\\. Method=AXSelectedText|Paste dispatch completed\\..*Verified=true" \
    "${log_segment_path}"; then
    echo "ORhom did not log a verified automatic insertion." >&2
    return 1
  fi
  if /usr/bin/grep -Eq \
    "Paste fallback|Paste diagnostic failed|Verified=false|Verified=dispatch-only" \
    "${log_segment_path}"; then
    echo "ORhom logged a fallback or unverified insertion." >&2
    return 1
  fi
  if ! /usr/bin/grep -Fq \
    "Paste diagnostic finished. Settled=true." \
    "${log_segment_path}"; then
    echo "ORhom did not settle the paste diagnostic." >&2
    return 1
  fi
}

orhom_test_assert_negative_log() {
  local log_segment_path="$1"
  local expected_bundle="$2"
  local expected_pid="$3"
  local expected_reason_pattern="$4"
  local require_no_dispatch="${5:-false}"
  if ! /usr/bin/grep -F "Focus captured." "${log_segment_path}" |
    /usr/bin/grep -Fq \
      "TargetPID=${expected_pid} Bundle='${expected_bundle}'"; then
    echo "ORhom did not capture the protected field in the expected process." >&2
    return 1
  fi
  if /usr/bin/grep -Eq \
    "Paste completed\\.|Paste dispatch completed\\." \
    "${log_segment_path}"; then
    echo "ORhom reported insertion success for a protected field." >&2
    return 1
  fi
  if ! /usr/bin/grep -Eq \
    "Paste fallback\\. Reason='(${expected_reason_pattern})" \
    "${log_segment_path}"; then
    echo "ORhom did not log an explicit protected-field refusal." >&2
    return 1
  fi
  if [[ "${require_no_dispatch}" == true ]] &&
     /usr/bin/grep -Fq "Paste dispatched." "${log_segment_path}"; then
    echo "ORhom dispatched a keyboard paste into a pre-protected field." >&2
    return 1
  fi
  if ! /usr/bin/grep -Fq \
    "Paste diagnostic finished. Settled=true." \
    "${log_segment_path}"; then
    echo "ORhom did not settle the protected-field refusal." >&2
    return 1
  fi
}
