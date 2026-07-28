#!/bin/zsh
set -euo pipefail

app_binary="/Applications/ORhom.app/Contents/MacOS/ORhom"
test_token="ORhomChatGPTDesktop$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"

restart_orhom() {
  /usr/bin/open /Applications/ORhom.app >/dev/null 2>&1 || true
}
trap restart_orhom EXIT

if [[ ! -x "${app_binary}" ]]; then
  echo "Install ORhom.app before running the ChatGPT integration test." >&2
  exit 1
fi
if ! /usr/bin/pgrep -f \
  '/Applications/ChatGPT 2.app/Contents/MacOS/ChatGPT' >/dev/null 2>&1; then
  echo "The ChatGPT desktop app (com.openai.chat) must be running." >&2
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
  echo "ORhom could not be closed for the ChatGPT paste test." >&2
  exit 1
fi

ORHOM_CHATGPT_TEST_TOKEN="${test_token}" /usr/bin/osascript <<'APPLESCRIPT'
set testToken to system attribute "ORHOM_CHATGPT_TEST_TOKEN"
set appBinary to "/Applications/ORhom.app/Contents/MacOS/ORhom"

tell application "System Events"
  set chatProcess to missing value
  repeat with candidate in every application process whose name is "ChatGPT"
    if (bundle identifier of candidate as text) is "com.openai.chat" then
      set chatProcess to candidate
    end if
  end repeat
  if chatProcess is missing value then
    error "The ChatGPT desktop process is missing."
  end if
  if (count windows of chatProcess) is 0 then
    key code 49 using option down
    delay 0.6
  end if

  set composer to value of attribute "AXFocusedUIElement" of chatProcess
  if role of composer is not "AXTextArea" then
    error "The ChatGPT composer is not focused."
  end if
  set elementPosition to position of composer
  set elementSize to size of composer
  set pointerX to round ((item 1 of elementPosition) + ¬
    ((item 1 of elementSize) / 2)) rounding down
  set pointerY to round ((item 2 of elementPosition) + ¬
    ((item 2 of elementSize) / 2)) rounding down
  set originalValue to value of composer as text
end tell

set diagnosticCommand to "/usr/bin/env ORHOM_DIAGNOSTIC_TEXT=" & ¬
  quoted form of testToken & " ORHOM_DIAGNOSTIC_POINTER_X=" & ¬
  quoted form of (pointerX as text) & " ORHOM_DIAGNOSTIC_POINTER_Y=" & ¬
  quoted form of (pointerY as text) & " " & quoted form of appBinary & ¬
  " --diagnose-paste"
do shell script diagnosticCommand

tell application "System Events"
  set chatProcess to missing value
  repeat with candidate in every application process whose name is "ChatGPT"
    if (bundle identifier of candidate as text) is "com.openai.chat" then
      set chatProcess to candidate
    end if
  end repeat
  set composer to value of attribute "AXFocusedUIElement" of chatProcess
  set insertedValue to value of composer as text
  set inserted to insertedValue contains testToken
  set value of composer to originalValue
  delay 0.1
  set restored to (value of composer as text) is originalValue
end tell

if not inserted then
  error "The ChatGPT composer did not receive the diagnostic text."
end if
if not restored then
  error "The ChatGPT composer could not be restored after the diagnostic."
end if
APPLESCRIPT

echo "ORhom ChatGPT desktop integration test passed; test text restored."
