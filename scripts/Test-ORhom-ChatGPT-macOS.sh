#!/bin/zsh
set -euo pipefail

app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
log_path="${HOME}/Library/Logs/ORhom/app.log"
test_token="ORhomChatGPTDesktop$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
underlying_marker="${test_token}UnderlyingTextEdit"
test_temp_dir="$(/usr/bin/mktemp -d -t orhom-chatgpt-test)"
clipboard_before_path="${test_temp_dir}/clipboard-before.json"
clipboard_after_path="${test_temp_dir}/clipboard-after.json"
new_log_path="${test_temp_dir}/new-log.txt"
log_start_size=0
original_frontmost_pid=""
textedit_was_running=false
test_document_may_exist=false

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

close_underlying_document_if_needed() {
  if [[ "${test_document_may_exist}" != true ]] ||
     ! /usr/bin/pgrep -x TextEdit >/dev/null 2>&1; then
    return
  fi

  local close_outcome
  if ! close_outcome="$(
  ORHOM_CHATGPT_UNDERLYING_MARKER="${underlying_marker}" \
  ORHOM_TEXTEDIT_WAS_RUNNING="${textedit_was_running}" \
    /usr/bin/osascript 2>/dev/null <<'APPLESCRIPT'
set underlyingMarker to system attribute "ORHOM_CHATGPT_UNDERLYING_MARKER"
set textEditWasRunning to (system attribute "ORHOM_TEXTEDIT_WAS_RUNNING") is "true"
set foundModifiedDocument to false
set closedOwnedDocument to false

tell application "TextEdit"
  repeat with candidateDocument in (every document)
    try
      set candidateText to text of candidateDocument as text
      if candidateText is underlyingMarker then
        close candidateDocument saving no
        set closedOwnedDocument to true
      else if candidateText contains underlyingMarker then
        set foundModifiedDocument to true
      end if
    end try
  end repeat
  if not foundModifiedDocument and not textEditWasRunning and (count documents) is 0 then
    quit
  end if
end tell

if foundModifiedDocument then
  return "modified"
else if closedOwnedDocument then
  return "closed"
end if
return "not-found"
APPLESCRIPT
  )"; then
    echo "Warning: the controlled TextEdit document could not be inspected safely; it was left open." >&2
    return
  fi
  if [[ "${close_outcome}" == "modified" ]]; then
    echo "Warning: the controlled TextEdit document contains unexpected text; it was left open without discarding changes." >&2
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

cleanup() {
  close_underlying_document_if_needed
  restore_clipboard_if_owned || {
    echo "Warning: the original clipboard could not be recovered." >&2
  }
  restart_orhom
  restore_original_frontmost
  /bin/rm -rf -- "${test_temp_dir}"
}
trap cleanup EXIT

if [[ ! -x "${app_binary}" ]]; then
  echo "Install the current ORhom.app before running the ChatGPT integration test." >&2
  exit 1
fi

original_frontmost_pid="$(
  /usr/bin/osascript \
    -e 'tell application "System Events" to return unix id of first application process whose frontmost is true' \
    2>/dev/null || true
)"
if [[ "${original_frontmost_pid}" != <-> ]]; then
  echo "The originally frontmost application could not be identified." >&2
  exit 1
fi
if /usr/bin/pgrep -x TextEdit >/dev/null 2>&1; then
  textedit_was_running=true
fi

snapshot_clipboard "${clipboard_before_path}"
test_document_may_exist=true

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
  ORHOM_CHATGPT_UNDERLYING_MARKER="${underlying_marker}" \
  ORHOM_TEST_APP_BINARY="${app_binary}" \
  ORHOM_ORIGINAL_FRONTMOST_PID="${original_frontmost_pid}" \
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

on waitForFocusedUnderlyingEditor(underlyingMarker)
  repeat 80 times
    tell application "System Events"
      set textEditProcesses to every application process whose bundle identifier is "com.apple.TextEdit"
      repeat with textEditProcess in textEditProcesses
        try
          if frontmost of textEditProcess then
            set focusedEditor to value of attribute "AXFocusedUIElement" of textEditProcess
            set focusedRole to role of focusedEditor as text
            set reportsFocused to value of attribute "AXFocused" of focusedEditor
            set editorValue to value of focusedEditor as text
            if focusedRole is "AXTextArea" and reportsFocused is true and editorValue is underlyingMarker then
              return true
            end if
          end if
        end try
      end repeat
    end tell
    delay 0.05
  end repeat
  return false
end waitForFocusedUnderlyingEditor

on underlyingEditorRemainsBeneathQuickChat(underlyingMarker)
  set chatAppProcess to my findChatProcess()
  tell application "System Events"
    if frontmost of chatAppProcess then
      return false
    end if
    set textEditProcesses to every application process whose bundle identifier is "com.apple.TextEdit"
    repeat with textEditProcess in textEditProcesses
      try
        if frontmost of textEditProcess then
          set focusedEditor to value of attribute "AXFocusedUIElement" of textEditProcess
          set focusedRole to role of focusedEditor as text
          set reportsFocused to value of attribute "AXFocused" of focusedEditor
          set editorValue to value of focusedEditor as text
          if focusedRole is "AXTextArea" and reportsFocused is true and editorValue is underlyingMarker then
            return true
          end if
        end if
      end try
    end repeat
  end tell
  return false
end underlyingEditorRemainsBeneathQuickChat

on underlyingDocumentIsUnchanged(createdDocument, underlyingMarker)
  if createdDocument is missing value then
    return true
  end if
  try
    tell application "TextEdit"
      return (text of createdDocument as text) is underlyingMarker
    end tell
  on error
    return false
  end try
end underlyingDocumentIsUnchanged

on closeUnderlyingDocument(createdDocument, underlyingMarker)
  if createdDocument is missing value then
    return true
  end if
  try
    tell application "TextEdit"
      set candidateText to text of createdDocument as text
      if candidateText is not underlyingMarker then
        return false
      end if
      close createdDocument saving no
    end tell
    return true
  on error
    return false
  end try
end closeUnderlyingDocument

on waitForUnderlyingEditorBeneathQuickChat(underlyingMarker)
  repeat 40 times
    if my underlyingEditorRemainsBeneathQuickChat(underlyingMarker) then
      return true
    end if
    delay 0.05
  end repeat
  return false
end waitForUnderlyingEditorBeneathQuickChat

on createUnderlyingDocument(underlyingMarker)
  tell application "TextEdit"
    activate
    set createdDocument to make new document with properties {text:underlyingMarker}
  end tell
  delay 0.35
  return createdDocument
end createUnderlyingDocument

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

on quickChatPanelIsVisible()
  set chatAppProcess to my findChatProcess()
  tell application "System Events"
    repeat with candidateWindow in windows of chatAppProcess
      try
        if (role of candidateWindow as text) is "AXWindow" and ¬
          (subrole of candidateWindow as text) is "AXSystemDialog" then
          return true
        end if
      end try
    end repeat
  end tell
  return false
end quickChatPanelIsVisible

on quickChatAccessibilitySummary()
  set chatAppProcess to my findChatProcess()
  set windowSummary to {}
  set focusedSummary to "missing"
  tell application "System Events"
    repeat with candidateWindow in windows of chatAppProcess
      try
        set end of windowSummary to ((role of candidateWindow as text) & "/" & (subrole of candidateWindow as text))
      on error
        set end of windowSummary to "unreadable"
      end try
    end repeat
    try
      set candidate to value of attribute "AXFocusedUIElement" of chatAppProcess
      if candidate is not missing value then
        set candidateRole to role of candidate as text
        set candidateSubrole to "unknown"
        set candidateDescription to ""
        try
          set candidateSubrole to subrole of candidate as text
        end try
        try
          set candidateDescription to description of candidate as text
        end try
        set focusedSummary to candidateRole & "/" & candidateSubrole & "/" & candidateDescription
      end if
    end try
  end tell
  return "windows=" & (windowSummary as text) & "; focused=" & focusedSummary
end quickChatAccessibilitySummary

on openQuickChatWithRetry()
  repeat with attemptNumber from 1 to 3
    if my quickChatPanelIsVisible() then
      return my waitForQuickChatOpen()
    end if

    my toggleQuickChat()
    set composer to my waitForQuickChatOpen()
    if composer is not missing value then
      return composer
    end if
    if my quickChatPanelIsVisible() then
      return my waitForQuickChatOpen()
    end if

    if attemptNumber < 3 then
      delay 0.35
      if my quickChatPanelIsVisible() then
        return my waitForQuickChatOpen()
      end if
    end if
  end repeat
  return missing value
end openQuickChatWithRetry

on waitForQuickChatClosed()
  repeat 80 times
    if not my quickChatPanelIsVisible() then
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
  set currentlyOpen to my quickChatPanelIsVisible()
  if initiallyOpen and not currentlyOpen then
    my toggleQuickChat()
    repeat 80 times
      if my quickChatPanelIsVisible() then
        return true
      end if
      delay 0.05
    end repeat
    return false
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

set testToken to system attribute "ORHOM_CHATGPT_TEST_TOKEN"
set underlyingMarker to system attribute "ORHOM_CHATGPT_UNDERLYING_MARKER"
set appBinary to system attribute "ORHOM_TEST_APP_BINARY"
set originalFrontmostProcessIdentifier to (system attribute "ORHOM_ORIGINAL_FRONTMOST_PID") as integer
set panelStateKnown to false
set panelInitiallyOpen to false
set testComposerWasPrepared to false
set createdUnderlyingDocument to missing value
set underlyingTextUnchanged to true
set underlyingDocumentClosed to true
set failureText to missing value
set failureNumber to 1

try
  set panelInitiallyOpen to my quickChatPanelIsVisible()
  set panelStateKnown to true
  if panelInitiallyOpen then
    set initialComposer to my waitForQuickChatOpen()
    if initialComposer is missing value then
      error "Quick Chat was already visible but its composer was not safely focused; the integration test did not modify it."
    end if
    set initialComposerValue to my quickChatComposerText(initialComposer)
    if initialComposerValue is not "" then
      error "Quick Chat already contains text. The integration test did not modify it."
    end if
  end if

  if panelInitiallyOpen then
    my toggleQuickChat()
    if not my waitForQuickChatClosed() then
      error "Option-Space did not close the existing empty Quick Chat panel."
    end if
  end if

  set createdUnderlyingDocument to my createUnderlyingDocument(underlyingMarker)
  if not my waitForFocusedUnderlyingEditor(underlyingMarker) then
    error "TextEdit did not expose the controlled document as its focused AXTextArea."
  end if

  set composer to my openQuickChatWithRetry()
  if composer is missing value then
    error "Option-Space did not expose a focused AXTextArea inside an AXSystemDialog. " & my quickChatAccessibilitySummary()
  end if
  if not my waitForUnderlyingEditorBeneathQuickChat(underlyingMarker) then
    error "Quick Chat did not remain a nonactivating panel above the focused TextEdit editor."
  end if
  if my quickChatComposerText(composer) is not "" then
    error "Quick Chat contains text. The integration test did not overwrite it."
  end if
  set testComposerWasPrepared to true

  set chatAppProcess to my findChatProcess()
  tell application "System Events"
    set chatProcessIdentifier to unix id of chatAppProcess
  end tell
  set diagnosticCommand to "/usr/bin/env -u ORHOM_DIAGNOSTIC_POINTER_X -u ORHOM_DIAGNOSTIC_POINTER_Y ORHOM_DIAGNOSTIC_SIMULATE_INTERVENING_INPUT=1 ORHOM_DIAGNOSTIC_TEXT=" & ¬
    quoted form of testToken & " ORHOM_DIAGNOSTIC_EXPECTED_BUNDLE=com.openai.chat ORHOM_DIAGNOSTIC_EXPECTED_PID=" & ¬
    quoted form of (chatProcessIdentifier as text) & " ORHOM_DIAGNOSTIC_TRACE_FOCUS=1 " & ¬
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
  if not my underlyingEditorRemainsBeneathQuickChat(underlyingMarker) then
    error "The controlled TextEdit editor lost its underlying focused state during the paste diagnostic."
  end if
on error errorText number errorNumber
  set failureText to errorText
  set failureNumber to errorNumber
end try

set underlyingTextUnchanged to my underlyingDocumentIsUnchanged(createdUnderlyingDocument, underlyingMarker)
set composerRestored to my clearOwnedTestComposer(testToken, testComposerWasPrepared)
set panelRestored to my restoreQuickChatState(panelStateKnown, panelInitiallyOpen, composerRestored)
set underlyingDocumentClosed to my closeUnderlyingDocument(createdUnderlyingDocument, underlyingMarker)
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
if not underlyingTextUnchanged then
  error "The controlled underlying TextEdit document was modified."
end if
if not underlyingDocumentClosed then
  error "The controlled underlying TextEdit document could not be closed without saving."
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

if ! /usr/bin/grep -Fq \
  "Paste diagnostic simulated intervening input." "${new_log_path}"; then
  echo "ORhom did not exercise the intervening-input Quick Chat path." >&2
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

echo "ORhom pointer-free ChatGPT Quick Chat integration test passed over a focused TextEdit editor after intervening input; underlying text, composer, clipboard, panel, and frontmost state restored."
