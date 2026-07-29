#!/bin/zsh
set -euo pipefail

app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
log_path="${HOME}/Library/Logs/ORhom/app.log"
test_token="ORhomChatGPTDesktop$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
test_temp_dir="$(/usr/bin/mktemp -d -t orhom-chatgpt-test)"
clipboard_before_path="${test_temp_dir}/clipboard-before.json"
clipboard_after_path="${test_temp_dir}/clipboard-after.json"
new_log_path="${test_temp_dir}/new-log.txt"
log_start_size=0

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
  outcome="$(
    /usr/bin/osascript -l JavaScript - \
      "${clipboard_before_path}" "${test_token}" <<'JXA'
ObjC.import("AppKit");
ObjC.import("Foundation");

function run(argv) {
  const snapshotPath = argv[0];
  const testToken = argv[1];
  const pasteboard = $.NSPasteboard.generalPasteboard;
  const currentString = pasteboard.stringForType(
    $.NSPasteboardTypeString
  );
  if (
    !currentString ||
    ObjC.unwrap(currentString) !== testToken
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
  )" || return 1
  if [[ "${outcome}" == "restored" ]]; then
    echo "Recovered the original clipboard after an interrupted test." >&2
  fi
}

restart_orhom() {
  /usr/bin/open "${app_path}" >/dev/null 2>&1 || true
}

cleanup() {
  restore_clipboard_if_owned || {
    echo "Warning: the original clipboard could not be recovered." >&2
  }
  restart_orhom
  /bin/rm -rf -- "${test_temp_dir}"
}
trap cleanup EXIT

if [[ ! -x "${app_binary}" ]]; then
  echo "Install the current ORhom.app before running the ChatGPT integration test." >&2
  exit 1
fi

snapshot_clipboard "${clipboard_before_path}"

/usr/bin/osascript \
  -e 'tell application id "at.orhom.mac" to quit' >/dev/null 2>&1 || true
for _ in {1..80}; do
  if ! /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
    break
  fi
  /bin/sleep 0.05
done
if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  echo "ORhom could not be closed for the ChatGPT paste test." >&2
  exit 1
fi

if [[ -f "${log_path}" ]]; then
  log_start_size="$(/usr/bin/stat -f '%z' "${log_path}")"
fi

apple_script_succeeded=true
if ! ORHOM_CHATGPT_TEST_TOKEN="${test_token}" \
  ORHOM_TEST_APP_BINARY="${app_binary}" \
  /usr/bin/osascript <<'APPLESCRIPT'
on findChatProcess()
  tell application "System Events"
    set candidates to every application process whose bundle identifier is "com.openai.chat"
    if (count candidates) is not 1 then
      error "Exactly one running ChatGPT desktop process with bundle identifier com.openai.chat is required."
    end if
    return item 1 of candidates
  end tell
end findChatProcess

on focusedQuickChatComposer()
  set chatAppProcess to my findChatProcess()
  tell application "System Events"
    try
      set candidate to value of attribute "AXFocusedUIElement" of chatAppProcess
      if candidate is missing value then
        return missing value
      end if
      set candidateRole to role of candidate as text
      if candidateRole is not "AXTextArea" then
        return missing value
      end if
      set candidateWindow to value of attribute "AXWindow" of candidate
      if candidateWindow is missing value then
        return missing value
      end if
      set windowRole to role of candidateWindow as text
      set windowSubrole to subrole of candidateWindow as text
      if windowRole is "AXWindow" and windowSubrole is "AXSystemDialog" then
        return candidate
      end if
    end try
  end tell
  return missing value
end focusedQuickChatComposer

on quickChatComposerText(composer)
  tell application "System Events"
    return value of composer as text
  end tell
end quickChatComposerText

on currentFrontmostProcessIdentifier()
  tell application "System Events"
    set frontApplications to every application process whose frontmost is true
    if (count frontApplications) is not 1 then
      error "Could not identify exactly one frontmost application."
    end if
    return unix id of item 1 of frontApplications
  end tell
end currentFrontmostProcessIdentifier

on activateFinder()
  tell application "Finder" to activate
  repeat 40 times
    tell application "System Events"
      set frontApplications to every application process whose frontmost is true
      if (count frontApplications) is 1 and bundle identifier of item 1 of frontApplications is "com.apple.finder" then
        return true
      end if
    end tell
    delay 0.05
  end repeat
  return false
end activateFinder

on toggleQuickChat()
  tell application "System Events"
    key code 49 using option down
  end tell
end toggleQuickChat

on waitForQuickChatOpen()
  repeat 80 times
    set composer to my focusedQuickChatComposer()
    if composer is not missing value then
      return composer
    end if
    delay 0.05
  end repeat
  return missing value
end waitForQuickChatOpen

on waitForQuickChatClosed()
  repeat 80 times
    if my focusedQuickChatComposer() is missing value then
      return true
    end if
    delay 0.05
  end repeat
  return false
end waitForQuickChatClosed

on clearOwnedTestComposer(testToken, testComposerWasPrepared)
  if not testComposerWasPrepared then
    return true
  end if
  repeat 20 times
    set composer to my focusedQuickChatComposer()
    if composer is not missing value then
      set currentValue to my quickChatComposerText(composer)
      if currentValue is "" then
        return true
      end if
      if currentValue is not testToken then
        return false
      end if
      tell application "System Events" to set value of composer to ""
      delay 0.10
      return my quickChatComposerText(composer) is ""
    end if
    delay 0.05
  end repeat
  return false
end clearOwnedTestComposer

on restoreQuickChatState(stateKnown, initiallyOpen, composerIsSafe)
  if not stateKnown then
    return true
  end if
  if not composerIsSafe then
    return false
  end if
  set composer to my focusedQuickChatComposer()
  set currentlyOpen to composer is not missing value
  if initiallyOpen and not currentlyOpen then
    my toggleQuickChat()
    return my waitForQuickChatOpen() is not missing value
  else if not initiallyOpen and currentlyOpen then
    my toggleQuickChat()
    return my waitForQuickChatClosed()
  end if
  return true
end restoreQuickChatState

on restoreFrontmostApplication(processIdentifier)
  tell application "System Events"
    set matchingApplications to every application process whose unix id is processIdentifier
    if (count matchingApplications) is not 1 then
      return false
    end if
    set frontmost of item 1 of matchingApplications to true
  end tell
  repeat 40 times
    try
      if my currentFrontmostProcessIdentifier() is processIdentifier then
        return true
      end if
    end try
    delay 0.05
  end repeat
  return false
end restoreFrontmostApplication

on finderRemainsUnderlyingApplication()
  set chatAppProcess to my findChatProcess()
  tell application "System Events"
    if frontmost of chatAppProcess then
      return false
    end if
    set frontApplications to every application process whose frontmost is true
    if (count frontApplications) is 1 and bundle identifier of item 1 of frontApplications is "com.apple.finder" then
      return true
    end if
  end tell
  return false
end finderRemainsUnderlyingApplication

set testToken to system attribute "ORHOM_CHATGPT_TEST_TOKEN"
set appBinary to system attribute "ORHOM_TEST_APP_BINARY"
set panelStateKnown to false
set panelInitiallyOpen to false
set testComposerWasPrepared to false
set originalFrontmostProcessIdentifier to missing value
set failureText to missing value
set failureNumber to 1

try
  set originalFrontmostProcessIdentifier to my currentFrontmostProcessIdentifier()
  set initialComposer to my focusedQuickChatComposer()
  set panelInitiallyOpen to initialComposer is not missing value
  set panelStateKnown to true
  if panelInitiallyOpen then
    set initialComposerValue to my quickChatComposerText(initialComposer)
    if initialComposerValue is not "" then
      error "Quick Chat already contains text. The integration test did not modify it."
    end if
  end if

  if not my activateFinder() then
    error "Finder could not become the controlled underlying application."
  end if

  if panelInitiallyOpen then
    my toggleQuickChat()
    if not my waitForQuickChatClosed() then
      error "Option-Space did not close the existing empty Quick Chat panel."
    end if
  end if

  my toggleQuickChat()
  set composer to my waitForQuickChatOpen()
  if composer is missing value then
    error "Option-Space did not expose a focused AXTextArea inside an AXSystemDialog."
  end if
  if not my finderRemainsUnderlyingApplication() then
    error "Quick Chat did not remain a nonactivating panel above Finder."
  end if
  if my quickChatComposerText(composer) is not "" then
    error "Quick Chat contains text. The integration test did not overwrite it."
  end if
  set testComposerWasPrepared to true

  set diagnosticCommand to "/usr/bin/env -u ORHOM_DIAGNOSTIC_POINTER_X -u ORHOM_DIAGNOSTIC_POINTER_Y -u ORHOM_DIAGNOSTIC_SIMULATE_INTERVENING_INPUT ORHOM_DIAGNOSTIC_TEXT=" & ¬
    quoted form of testToken & " ORHOM_DIAGNOSTIC_EXPECTED_BUNDLE=com.openai.chat ORHOM_DIAGNOSTIC_TRACE_FOCUS=1 " & ¬
    quoted form of appBinary & " --diagnose-paste"
  do shell script diagnosticCommand

  set insertedComposer to missing value
  repeat 20 times
    set insertedComposer to my focusedQuickChatComposer()
    if insertedComposer is not missing value then
      exit repeat
    end if
    delay 0.05
  end repeat
  if insertedComposer is missing value then
    error "The Quick Chat composer disappeared during the paste diagnostic."
  end if
  set insertedValue to my quickChatComposerText(insertedComposer)
  if insertedValue is not testToken then
    error "The ChatGPT Quick Chat composer does not contain exactly the diagnostic token."
  end if
on error errorText number errorNumber
  set failureText to errorText
  set failureNumber to errorNumber
end try

set composerRestored to my clearOwnedTestComposer(testToken, testComposerWasPrepared)
set panelRestored to my restoreQuickChatState(panelStateKnown, panelInitiallyOpen, composerRestored)
set frontmostRestored to true
if originalFrontmostProcessIdentifier is not missing value then
  set frontmostRestored to my restoreFrontmostApplication(originalFrontmostProcessIdentifier)
end if

if failureText is not missing value then
  error failureText number failureNumber
end if
if not composerRestored then
  error "Quick Chat contains unexpected text; the test left it visible and did not overwrite it."
end if
if not panelRestored then
  error "The original ChatGPT Quick Chat visibility could not be restored."
end if
if not frontmostRestored then
  error "The originally frontmost application could not be restored."
end if
APPLESCRIPT
then
  apple_script_succeeded=false
fi

snapshot_clipboard "${clipboard_after_path}"
if ! /usr/bin/cmp -s "${clipboard_before_path}" "${clipboard_after_path}"; then
  echo "The ChatGPT paste diagnostic did not restore the clipboard exactly." >&2
  exit 1
fi

if [[ ! -f "${log_path}" ]]; then
  echo "ORhom did not create its diagnostic log." >&2
  exit 1
fi
log_current_size="$(/usr/bin/stat -f '%z' "${log_path}")"
if (( log_current_size < log_start_size )); then
  log_start_size=0
fi
/usr/bin/tail -c "+$((log_start_size + 1))" "${log_path}" >"${new_log_path}"

if [[ "${apple_script_succeeded}" != true ]]; then
  echo "The pointer-free ChatGPT Quick Chat integration test failed." >&2
  /bin/cat "${new_log_path}" >&2
  exit 1
fi

focus_line="$(
  /usr/bin/grep -F "Focus captured." "${new_log_path}" |
    /usr/bin/tail -n 1
)" || true
if [[ -z "${focus_line}" ||
      "${focus_line}" != *"Bundle='com.openai.chat'"* ||
      "${focus_line}" != *"NonactivatingWindow=true"* ||
      "${focus_line}" != *"Role='AXTextArea'"* ]]; then
  echo "ORhom did not log a safe nonactivating ChatGPT AXTextArea target." >&2
  /bin/cat "${new_log_path}" >&2
  exit 1
fi

if /usr/bin/grep -Eq \
  "Paste fallback|Paste diagnostic failed|Paste failed|Verified=false" \
  "${new_log_path}"; then
  echo "ORhom logged a paste fallback or failed verification." >&2
  /bin/cat "${new_log_path}" >&2
  exit 1
fi

ax_success_line="$(
  /usr/bin/grep -F "Paste completed. Method=AXSelectedText" \
    "${new_log_path}" |
    /usr/bin/tail -n 1
)" || true
keyboard_success_line="$(
  /usr/bin/grep -F "Paste dispatch completed." "${new_log_path}" |
    /usr/bin/tail -n 1
)" || true
if [[ -n "${ax_success_line}" &&
      "${ax_success_line}" == *"ClipboardChanged=false"* ]]; then
  :
elif [[ -n "${keyboard_success_line}" &&
        "${keyboard_success_line}" == *"Verified=true"* &&
        "${keyboard_success_line}" == *"ClipboardRestored=true"* ]]; then
  :
else
  echo "ORhom did not log a confirmed Quick Chat paste." >&2
  /bin/cat "${new_log_path}" >&2
  exit 1
fi

if ! /usr/bin/grep -Fq \
  "Paste diagnostic finished. Settled=true." "${new_log_path}"; then
  echo "ORhom did not settle the Quick Chat paste diagnostic." >&2
  /bin/cat "${new_log_path}" >&2
  exit 1
fi

echo "ORhom pointer-free ChatGPT Quick Chat integration test passed; composer, clipboard, and panel state restored."
