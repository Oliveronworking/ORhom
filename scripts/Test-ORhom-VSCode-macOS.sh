#!/bin/zsh
set -euo pipefail

app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
vscode_app="/Applications/Visual Studio Code.app"
vscode_binary="${vscode_app}/Contents/MacOS/Code"
log_path="${HOME}/Library/Logs/ORhom/app.log"
run_id="$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
test_temp_dir="$(/usr/bin/mktemp -d /tmp/orh-vsc.XXXXXX)"
test_file="${test_temp_dir}/ORhom-VSCode-${run_id}.txt"
vscode_user_data="${test_temp_dir}/vscode-user-data"
vscode_extensions="${test_temp_dir}/vscode-extensions"
vscode_shared_data="${test_temp_dir}/vscode-shared-data"
vscode_output="${test_temp_dir}/vscode.log"
clipboard_before_path="${test_temp_dir}/clipboard-before.json"
clipboard_after_path="${test_temp_dir}/clipboard-after.json"
clipboard_sentinel="ORhomVSCodeClipboard${run_id}"
test_token="ORhomVSCodeEditor${run_id}"
expected_text="LEFT${test_token}RIGHT"
original_frontmost_pid="$(
  /usr/bin/osascript \
    -e 'tell application "System Events" to return unix id of first application process whose frontmost is true' \
    2>/dev/null || true
)"
orhom_was_running=false
clipboard_snapshot_captured=false
clipboard_restored=false
clipboard_restore_outcome=""
vscode_cli_pid=""
vscode_process_pid=""
vscode_window_owned=false
vscode_window_closed=false

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
      "${test_token}" <<'JXA'
ObjC.import("AppKit");
ObjC.import("Foundation");

function run(argv) {
  const snapshotPath = argv[0];
  const sentinel = argv[1];
  const testToken = argv[2];
  const pasteboard = $.NSPasteboard.generalPasteboard;
  const currentString = pasteboard.stringForType(
    $.NSPasteboardTypeString
  );
  if (!currentString) {
    return "skipped";
  }
  const current = ObjC.unwrap(currentString);
  if (current !== sentinel && current !== testToken) {
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

owned_window_identity() {
  ORHOM_VSCODE_RUN_ID="${run_id}" \
    /usr/bin/osascript <<'APPLESCRIPT'
set runID to system attribute "ORHOM_VSCODE_RUN_ID"
tell application "System Events"
  set foundItems to {}
  set codeProcesses to every application process whose bundle identifier is "com.microsoft.VSCode"
  repeat with codeProcess in codeProcesses
    repeat with candidateWindow in (every window of codeProcess)
      try
        set candidateTitle to title of candidateWindow as text
        if candidateTitle contains runID then
          set end of foundItems to ((unix id of codeProcess as text) & "|" & candidateTitle)
        end if
      end try
    end repeat
  end repeat
  if (count foundItems) is 1 then
    return item 1 of foundItems
  end if
  return ""
end tell
APPLESCRIPT
}

focus_owned_editor() {
  ORHOM_VSCODE_PID="${vscode_process_pid}" \
    /usr/bin/osascript -l JavaScript <<'JXA' >/dev/null
ObjC.import("AppKit");

function run() {
  const pid = Number(
    ObjC.unwrap(
      $.NSProcessInfo.processInfo.environment.objectForKey(
        "ORHOM_VSCODE_PID"
      )
    )
  );
  const application =
    $.NSRunningApplication.runningApplicationWithProcessIdentifier(pid);
  if (!application) {
    throw new Error("The isolated VS Code process is unavailable.");
  }
  if (!application.activateWithOptions(2)) {
    throw new Error("The isolated VS Code process could not be activated.");
  }
}
JXA
  /bin/sleep 0.35
  ORHOM_VSCODE_PID="${vscode_process_pid}" \
    /usr/bin/osascript -l JavaScript <<'JXA'
ObjC.import("AppKit");

function run() {
  const expectedPID = Number(
    ObjC.unwrap(
      $.NSProcessInfo.processInfo.environment.objectForKey(
        "ORHOM_VSCODE_PID"
      )
    )
  );
  const frontmost = $.NSWorkspace.sharedWorkspace.frontmostApplication;
  if (!frontmost || Number(frontmost.processIdentifier) !== expectedPID) {
    throw new Error(
      "The isolated VS Code process lost focus before the diagnostic."
    );
  }
  return "goto-caret-prepared";
}
JXA
}

close_owned_window_best_effort() {
  if [[ "${vscode_window_owned}" != true ||
        "${vscode_process_pid}" != <-> ]]; then
    return
  fi

  local command_text
  command_text="$(
    /bin/ps -ww -p "${vscode_process_pid}" -o command= 2>/dev/null || true
  )"
  if [[ -z "${command_text}" ]]; then
    vscode_window_closed=true
    return
  fi
  if [[ "${command_text}" != *"${vscode_user_data}"* ]] ||
     [[ "${command_text}" != *"${vscode_app}/Contents/MacOS/Code"* ]]; then
    return
  fi

  /bin/kill -TERM "${vscode_process_pid}" >/dev/null 2>&1 || true
  for _ in {1..80}; do
    if ! /bin/kill -0 "${vscode_process_pid}" >/dev/null 2>&1; then
      vscode_window_closed=true
      return
    fi
    /bin/sleep 0.05
  done

  command_text="$(
    /bin/ps -ww -p "${vscode_process_pid}" -o command= 2>/dev/null || true
  )"
  if [[ "${command_text}" == *"${vscode_user_data}"* ]]; then
    /bin/kill -KILL "${vscode_process_pid}" >/dev/null 2>&1 || true
    vscode_window_closed=true
  fi
}

cleanup() {
  close_owned_window_best_effort
  if [[ "${vscode_cli_pid}" == <-> ]] &&
     /bin/kill -0 "${vscode_cli_pid}" >/dev/null 2>&1; then
    /bin/kill -TERM "${vscode_cli_pid}" >/dev/null 2>&1 || true
  fi
  restore_orhom_state
  if [[ "${clipboard_restored}" != true ]]; then
    if ! restore_clipboard_if_owned; then
      echo "Warning: the original clipboard could not be recovered." >&2
    elif [[ "${clipboard_restore_outcome}" == "skipped" ]]; then
      echo "The clipboard changed externally; the test preserved that newer content." >&2
    fi
  fi
  restore_original_frontmost
  if [[ "${vscode_window_owned}" != true ||
        "${vscode_window_closed}" == true ]]; then
    /bin/rm -rf -- "${test_temp_dir}"
  else
    echo "The isolated VS Code window could not be closed; its temporary directory remains at ${test_temp_dir}." >&2
  fi
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

if [[ ! -x "${app_binary}" ]]; then
  echo "Install the current ORhom.app before running the VS Code test." >&2
  exit 1
fi
if [[ ! -x "${vscode_binary}" ]]; then
  echo "Visual Studio Code is not installed at the expected path." >&2
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
  echo "ORhom could not be stopped for the VS Code test." >&2
  exit 1
fi

snapshot_clipboard "${clipboard_before_path}"
clipboard_snapshot_captured=true
set_clipboard_sentinel

/bin/mkdir -p \
  "${vscode_user_data}/User" \
  "${vscode_extensions}" \
  "${vscode_shared_data}"
/usr/bin/printf '%s\n' \
  '{"files.autoSave":"afterDelay","files.autoSaveDelay":100}' \
  >"${vscode_user_data}/User/settings.json"
/usr/bin/printf '%s' "LEFTRIGHT" >"${test_file}"

/usr/bin/env \
  -u ELECTRON_RUN_AS_NODE \
  -u VSCODE_CODE_CACHE_PATH \
  -u VSCODE_CWD \
  -u VSCODE_ESM_ENTRYPOINT \
  -u VSCODE_HANDLES_UNCAUGHT_ERRORS \
  -u VSCODE_IPC_HOOK \
  -u VSCODE_IPC_HOOK_CLI \
  -u VSCODE_NLS_CONFIG \
  -u VSCODE_PID \
  /usr/bin/open -n -a "${vscode_app}" \
  --args \
  --user-data-dir="${vscode_user_data}" \
  --extensions-dir="${vscode_extensions}" \
  --shared-data-dir="${vscode_shared_data}" \
  --disable-extensions \
  --disable-layout-restore \
  --new-window \
  --skip-add-to-recently-opened \
  --skip-welcome \
  --goto \
  "${test_file}:1:5" \
  >"${vscode_output}" 2>&1

for _ in {1..160}; do
  vscode_process_pid="$(
    /usr/bin/pgrep -f \
      "${vscode_app}/Contents/MacOS/Code --user-data-dir=${vscode_user_data}" \
      2>/dev/null |
      /usr/bin/head -n 1 ||
      true
  )"
  if [[ "${vscode_process_pid}" == <-> ]] &&
     /bin/kill -0 "${vscode_process_pid}" >/dev/null 2>&1; then
    break
  fi
  /bin/sleep 0.05
done
if [[ "${vscode_process_pid}" != <-> ]]; then
  echo "The isolated VS Code process did not appear." >&2
  /bin/cat "${vscode_output}" >&2 || true
  /usr/bin/pgrep -fl "${vscode_user_data}" >&2 || true
  exit 1
fi
vscode_window_owned=true
/bin/sleep 1.5

focused_identity="$(focus_owned_editor)"
log_size_before=0
if [[ -f "${log_path}" ]]; then
  log_size_before="$(/usr/bin/stat -f '%z' "${log_path}")"
fi

ORHOM_DIAGNOSTIC_TEXT="${test_token}" \
ORHOM_DIAGNOSTIC_EXPECTED_BUNDLE="com.microsoft.VSCode" \
ORHOM_DIAGNOSTIC_EXPECTED_PID="${vscode_process_pid}" \
ORHOM_DIAGNOSTIC_TRACE_FOCUS=1 \
  "${app_binary}" --diagnose-paste

new_log="$(new_log_since "${log_size_before}")"
if [[ "${new_log}" == *"Paste fallback"* ||
      "${new_log}" == *"Paste diagnostic failed"* ||
      "${new_log}" == *"Paste failed"* ||
      "${new_log}" == *"Verified=false"* ]]; then
  echo "ORhom logged a fallback or failed verification for VS Code." >&2
  exit 1
fi
if [[ "${new_log}" != *"Paste dispatched."* ||
      "${new_log}" != *"Paste dispatch completed."* ||
      "${new_log}" != *"Verified=dispatch-only"* ||
      "${new_log}" != *"ClipboardRestored=true"* ]]; then
  echo "ORhom did not complete a VS Code keyboard paste dispatch." >&2
  exit 1
fi
if [[ "${new_log}" != *"Temporary accessibility exposure enabled."* ]] ||
   (
     [[ "${new_log}" != *"Temporary accessibility exposure restored."* ]] &&
     [[ "${new_log}" != *"Temporary accessibility exposure already inactive."* ]]
   ); then
  echo "ORhom did not enable and restore VS Code accessibility for exactly this diagnostic session." >&2
  exit 1
fi
if [[ "${new_log}" != *"Paste diagnostic finished. Settled=true."* ]]; then
  echo "The VS Code paste diagnostic did not settle." >&2
  exit 1
fi

for _ in {1..120}; do
  file_text="$(/bin/cat "${test_file}")"
  if [[ "${file_text}" == "${expected_text}" ]]; then
    break
  fi
  /bin/sleep 0.05
done
if [[ "${file_text:-}" != "${expected_text}" ]]; then
  echo "The isolated VS Code editor did not receive the token at the prepared caret." >&2
  echo "Expected: ${expected_text}" >&2
  echo "Actual:   ${file_text:-}" >&2
  exit 1
fi
if [[ "$(
  /usr/bin/pbpaste 2>/dev/null || true
)" != "${clipboard_sentinel}" ]]; then
  echo "The clipboard changed during the VS Code test; newer content was preserved." >&2
  exit 1
fi

close_owned_window_best_effort
if [[ "${vscode_window_closed}" != true ]]; then
  echo "The isolated VS Code test window could not be closed safely." >&2
  exit 1
fi

if ! restore_clipboard_if_owned; then
  echo "The original clipboard could not be restored after the VS Code test." >&2
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
  echo "The VS Code test did not restore the clipboard exactly." >&2
  exit 1
fi

echo "PASS VS Code isolated editor (${focused_identity}, exact temp-file insertion, clipboard restored)"
