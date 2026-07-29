#!/bin/zsh
set -euo pipefail

app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
log_path="${HOME}/Library/Logs/ORhom/app.log"
test_id="$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
test_token="ORhomTextEditTest${test_id}"
test_marker="ORhomTextEditDocument${test_id}"
clipboard_sentinel="ORhomClipboardSentinel${test_id}"
test_temp_dir="$(/usr/bin/mktemp -d -t orhom-textedit-test)"
clipboard_before_path="${test_temp_dir}/clipboard-before.json"
clipboard_after_path="${test_temp_dir}/clipboard-after.json"
clipboard_restore_outcome=""
test_document_may_exist=false
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

restore_clipboard_if_owned() {
  local outcome
  if ! outcome="$(
    /usr/bin/osascript -l JavaScript - \
      "${clipboard_before_path}" "${clipboard_sentinel}" \
      "${test_token}" <<'JXA'
ObjC.import("AppKit");
ObjC.import("Foundation");

function run(argv) {
  const snapshotPath = argv[0];
  const clipboardSentinel = argv[1];
  const testToken = argv[2];
  const pasteboard = $.NSPasteboard.generalPasteboard;
  const currentString = pasteboard.stringForType(
    $.NSPasteboardTypeString
  );
  if (
    !currentString ||
    (
      ObjC.unwrap(currentString) !== clipboardSentinel &&
      ObjC.unwrap(currentString) !== testToken
    )
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
  )"; then
    return 1
  fi
  clipboard_restore_outcome="${outcome}"
}

close_test_document_if_needed() {
  if [[ "${test_document_may_exist}" != true ]]; then
    return
  fi

  ORHOM_TEXTEDIT_TEST_MARKER="${test_marker}" \
    /usr/bin/osascript >/dev/null 2>&1 <<'APPLESCRIPT' || true
set testMarker to system attribute "ORHOM_TEXTEDIT_TEST_MARKER"

tell application "TextEdit"
  repeat with candidateDocument in (every document)
    try
      if (text of candidateDocument as text) contains testMarker then
        close candidateDocument saving no
      end if
    end try
  end repeat
end tell
APPLESCRIPT
}

restart_orhom_best_effort() {
  /usr/bin/open "${app_path}" >/dev/null 2>&1 || true
}

stop_orhom_best_effort() {
  if ! /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
    return
  fi
  /usr/bin/osascript \
    -e 'tell application id "at.orhom.mac" to quit' \
    >/dev/null 2>&1 || true
  for _ in {1..80}; do
    if ! /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
      return
    fi
    /bin/sleep 0.05
  done
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

cleanup() {
  close_test_document_if_needed
  stop_orhom_best_effort
  if restore_clipboard_if_owned; then
    if [[ "${clipboard_restore_outcome}" == "restored" ]]; then
      echo "Recovered the original clipboard after an interrupted test." >&2
    fi
  else
    echo "Warning: the original clipboard could not be recovered." >&2
  fi
  restart_orhom_best_effort
  restore_original_frontmost
  /bin/rm -rf -- "${test_temp_dir}"
}
trap cleanup EXIT

if [[ ! -x "${app_binary}" ]]; then
  echo "Install ORhom.app before running the TextEdit integration test." >&2
  exit 1
fi

snapshot_clipboard "${clipboard_before_path}"

if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  /usr/bin/osascript \
    -e 'tell application id "at.orhom.mac" to quit' >/dev/null 2>&1 || true
fi
for _ in {1..80}; do
  if ! /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
    break
  fi
  /bin/sleep 0.05
done
if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  echo "ORhom could not be closed for the TextEdit paste test." >&2
  exit 1
fi

log_size_before=0
if [[ -f "${log_path}" ]]; then
  log_size_before="$(/usr/bin/stat -f '%z' "${log_path}")"
fi

test_document_may_exist=true
ORHOM_TEXTEDIT_TEST_TOKEN="${test_token}" \
ORHOM_TEXTEDIT_TEST_MARKER="${test_marker}" \
ORHOM_TEXTEDIT_CLIPBOARD_SENTINEL="${clipboard_sentinel}" \
ORHOM_APP_PATH="${app_path}" \
  /usr/bin/osascript >/dev/null <<'APPLESCRIPT'
on closeCreatedDocument(createdDocument)
  if createdDocument is missing value then
    return
  end if
  tell application "TextEdit"
    close createdDocument saving no
  end tell
end closeCreatedDocument

set testToken to system attribute "ORHOM_TEXTEDIT_TEST_TOKEN"
set testMarker to system attribute "ORHOM_TEXTEDIT_TEST_MARKER"
set clipboardSentinel to system attribute "ORHOM_TEXTEDIT_CLIPBOARD_SENTINEL"
set appPath to system attribute "ORHOM_APP_PATH"
set createdDocument to missing value

try
  tell application "TextEdit"
    activate
    set createdDocument to make new document with properties {text:testMarker}
  end tell
  delay 0.35

  tell application "TextEdit"
    if (text of front document as text) does not contain testMarker then
      error "The temporary TextEdit document is not the front document."
    end if
  end tell

  tell application "System Events"
    tell application process "TextEdit"
      set frontmost to true
      if not (exists front window) then
        error "TextEdit did not expose a front window."
      end if
      if frontmost is not true then
        error "TextEdit did not become the frontmost application."
      end if

      set focusedEditor to value of attribute "AXFocusedUIElement"
      set focusedRole to role of focusedEditor as text
      if focusedRole is not "AXTextArea" then
        error "The focused TextEdit element is not a native AXTextArea; role is " & focusedRole & "."
      end if
      if (value of attribute "AXFocused" of focusedEditor) is not true then
        error "The native TextEdit AXTextArea does not report focused state."
      end if

      key code 125 using command down
    end tell
  end tell
  delay 0.10

  set the clipboard to clipboardSentinel
  if (the clipboard as text) is not clipboardSentinel then
    error "The clipboard sentinel could not be prepared."
  end if

  set diagnosticEnvironment to quoted form of ("ORHOM_DIAGNOSTIC_TEXT=" & testToken)
  set diagnosticCommand to "/usr/bin/open -g -W -a " & ¬
    quoted form of appPath & " --env " & diagnosticEnvironment & ¬
    " --env ORHOM_DIAGNOSTIC_EXPECTED_BUNDLE=com.apple.TextEdit" & ¬
    " --args --diagnose-paste"
  do shell script diagnosticCommand

  tell application "TextEdit"
    set insertedText to text of createdDocument as text
  end tell
  if insertedText does not contain testMarker then
    error "The diagnostic paste replaced the TextEdit test marker."
  end if
  if insertedText does not contain testToken then
    error "The focused native TextEdit field did not receive the diagnostic token."
  end if
  if (the clipboard as text) is not clipboardSentinel then
    error "ORhom did not preserve and restore the clipboard sentinel."
  end if

  my closeCreatedDocument(createdDocument)
  set createdDocument to missing value
on error errorMessage number errorNumber
  try
    my closeCreatedDocument(createdDocument)
  end try
  error errorMessage number errorNumber
end try
APPLESCRIPT

if ! restore_clipboard_if_owned; then
  echo "The original clipboard could not be restored after the TextEdit test." >&2
  exit 1
fi
if [[ "${clipboard_restore_outcome}" != "restored" ]]; then
  echo "The clipboard changed externally before the TextEdit test could restore it." >&2
  exit 1
fi
snapshot_clipboard "${clipboard_after_path}"
if ! /usr/bin/cmp -s "${clipboard_before_path}" "${clipboard_after_path}"; then
  echo "The TextEdit paste diagnostic did not restore the clipboard exactly." >&2
  exit 1
fi

if [[ ! -f "${log_path}" ]]; then
  echo "ORhom did not create its application log during the TextEdit test." >&2
  exit 1
fi

log_size_after="$(/usr/bin/stat -f '%z' "${log_path}")"
if (( log_size_after < log_size_before )); then
  log_size_before=0
fi
new_log="$(
  /usr/bin/tail -c "+$((log_size_before + 1))" "${log_path}"
)"

if ! /usr/bin/printf '%s\n' "${new_log}" |
  /usr/bin/grep -Fq "Bundle='com.apple.TextEdit'"; then
  echo "ORhom did not log capture of the TextEdit target." >&2
  exit 1
fi
if ! /usr/bin/printf '%s\n' "${new_log}" |
  /usr/bin/grep -Fq "Paste completed. Method=AXSelectedText"; then
  echo "ORhom did not log a successful native AXSelectedText paste." >&2
  exit 1
fi
if ! /usr/bin/printf '%s\n' "${new_log}" |
  /usr/bin/grep -Fq "Paste diagnostic finished. Settled=true."; then
  echo "ORhom did not log a settled TextEdit paste diagnostic." >&2
  exit 1
fi

close_test_document_if_needed
test_document_may_exist=false
restart_orhom_best_effort
for _ in {1..80}; do
  if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
    restore_original_frontmost
    /bin/rm -rf -- "${test_temp_dir}"
    trap - EXIT
    echo "ORhom TextEdit integration test passed; test document closed, clipboard restored, and ORhom restarted."
    exit 0
  fi
  /bin/sleep 0.05
done

echo "The TextEdit integration test passed, but ORhom did not restart." >&2
exit 1
