#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
source "${script_dir}/lib/ORhomTextFieldTestSupport.zsh"

app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
chrome_binary="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
fixture_path="${repo_dir}/macos/tests/fixtures/text-field-matrix.html"
ax_field_probe_source="${repo_dir}/macos/tests/fixtures/ORhomAXFieldProbe.swift"
log_path="${HOME}/Library/Logs/ORhom/app.log"
test_temp_dir="$(/usr/bin/mktemp -d -t orhom-chrome-field-matrix)"
ax_field_probe_binary="${test_temp_dir}/ORhomAXFieldProbe"
clipboard_before_path="${test_temp_dir}/clipboard-before.json"
clipboard_after_path="${test_temp_dir}/clipboard-after.json"
run_identifier="$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
orhom_test_run_prefix="ORhomWebMatrix${run_identifier}"
original_frontmost_pid="$(orhom_test_capture_frontmost_pid)"
active_chrome_pid=""
active_chrome_profile=""
clipboard_recovered=false
orhom_was_running=false
test_case_filter="${ORHOM_TEST_CASE_FILTER:-}"
insertion_case_count=0
refusal_case_count=0

if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  orhom_was_running=true
fi

fixture_base_url="$(
  ORHOM_WEB_FIXTURE_PATH="${fixture_path}" \
    /usr/bin/osascript -l JavaScript - <<'JXA'
ObjC.import("Foundation");

function run() {
  const path = ObjC.unwrap(
    $.NSProcessInfo.processInfo.environment.objectForKey(
      "ORHOM_WEB_FIXTURE_PATH"
    )
  );
  return ObjC.unwrap($.NSURL.fileURLWithPath($(path)).absoluteString);
}
JXA
)"

terminate_active_chrome() {
  if [[ "${active_chrome_pid}" != <-> ]]; then
    active_chrome_pid=""
    active_chrome_profile=""
    return
  fi
  if /bin/kill -0 "${active_chrome_pid}" >/dev/null 2>&1; then
    local active_command
    active_command="$(
      /bin/ps -p "${active_chrome_pid}" -o command= 2>/dev/null ||
        true
    )"
    if [[ "${active_command}" != *"${chrome_binary}"* ]] ||
       [[ "${active_command}" != *"--user-data-dir=${active_chrome_profile}"* ]]; then
      echo \
        "Warning: isolated Chrome PID ownership changed; the process was not terminated." \
        >&2
      active_chrome_pid=""
      active_chrome_profile=""
      return
    fi
    /bin/kill -TERM "${active_chrome_pid}" >/dev/null 2>&1 || true
    for _ in {1..100}; do
      if ! /bin/kill -0 "${active_chrome_pid}" >/dev/null 2>&1; then
        break
      fi
      /bin/sleep 0.05
    done
  fi
  active_chrome_pid=""
  active_chrome_profile=""
}

cleanup() {
  orhom_test_stop_orhom >/dev/null 2>&1 || true
  terminate_active_chrome
  if [[ "${clipboard_recovered}" != true ]]; then
    restore_outcome="$(
      orhom_test_restore_clipboard_if_owned \
        "${clipboard_before_path}" \
        "${orhom_test_run_prefix}" 2>/dev/null || true
    )"
    if [[ "${restore_outcome}" != "restored" ]]; then
      echo "Warning: the original clipboard could not be recovered." >&2
    fi
  fi
  if [[ "${orhom_was_running}" == true ]]; then
    orhom_test_restart_orhom "${app_path}" >/dev/null 2>&1 || true
  fi
  orhom_test_restore_frontmost_pid "${original_frontmost_pid}"
  /bin/rm -rf -- "${test_temp_dir}"
}
trap cleanup EXIT

chrome_page_title() {
  local targets_path="${test_temp_dir}/chrome-targets.json"
  local debugging_port
  debugging_port="$(/usr/bin/head -n 1 \
    "${active_chrome_profile}/DevToolsActivePort")"
  /usr/bin/curl \
    -fsS \
    --max-time 1 \
    "http://127.0.0.1:${debugging_port}/json/list" \
    -o "${targets_path}"

  ORHOM_WEB_TARGETS_PATH="${targets_path}" \
  ORHOM_WEB_FIXTURE_URL="${fixture_base_url}" \
    /usr/bin/osascript -l JavaScript - <<'JXA'
ObjC.import("Foundation");

function environmentValue(name) {
  return ObjC.unwrap(
    $.NSProcessInfo.processInfo.environment.objectForKey(name)
  );
}

function run() {
  const targetsPath = environmentValue("ORHOM_WEB_TARGETS_PATH");
  const fixtureURL = environmentValue("ORHOM_WEB_FIXTURE_URL");
  const data = $.NSData.dataWithContentsOfFile($(targetsPath));
  if (!data) {
    return "";
  }
  const text = $.NSString.alloc.initWithDataEncoding(
    data,
    $.NSUTF8StringEncoding
  );
  const targets = JSON.parse(ObjC.unwrap(text));
  const target = targets.find((candidate) =>
    candidate.type === "page" &&
    candidate.url.startsWith(fixtureURL)
  );
  return target ? target.title : "";
}
JXA
}

parse_web_title() {
  local title="$1"
  local expected_case="$2"
  local expected_value="$3"
  local expected_focus="$4"
  local expected_prefix=
  title="${title//$'\r'/}"
  expected_prefix="ORHOM_WEB:${expected_case}:${expected_value}:${expected_focus}:"
  [[ "${title}" == "${expected_prefix}"<->:<-> ]]
}

wait_for_web_state() {
  local expected_case="$1"
  local expected_value="$2"
  local expected_focus="$3"
  local title=""
  for _ in {1..200}; do
    title="$(chrome_page_title 2>/dev/null || true)"
    if parse_web_title \
      "${title}" \
      "${expected_case}" \
      "${expected_value}" \
      "${expected_focus}"; then
      /usr/bin/printf '%s' "${title}"
      return
    fi
    /bin/sleep 0.05
  done
  echo "Chrome fixture did not publish the expected state." >&2
  echo "Last page title: ${title}" >&2
  return 1
}

launch_isolated_chrome() {
  local field_case="$1"
  local initial_value="$2"
  local expected_focus="$3"
  local profile_case="${4:-${field_case}}"
  local fixture_url="${fixture_base_url}#case=${field_case}&initial=${initial_value}"
  local chrome_log_path="${test_temp_dir}/${field_case}-chrome.log"
  active_chrome_profile="${test_temp_dir}/chrome-${profile_case}"
  /bin/mkdir -p "${active_chrome_profile}"

  "${chrome_binary}" \
    --user-data-dir="${active_chrome_profile}" \
    --remote-debugging-port=0 \
    --no-first-run \
    --no-default-browser-check \
    --disable-default-apps \
    --disable-extensions \
    --disable-component-update \
    --disable-session-crashed-bubble \
    --disable-search-engine-choice-screen \
    --use-mock-keychain \
    --new-window \
    "${fixture_url}" \
    >"${chrome_log_path}" 2>&1 &
  active_chrome_pid=$!

  for _ in {1..240}; do
    if [[ -f "${active_chrome_profile}/DevToolsActivePort" ]] &&
       /bin/kill -0 "${active_chrome_pid}" >/dev/null 2>&1; then
      break
    fi
    /bin/sleep 0.05
  done
  if [[ ! -f "${active_chrome_profile}/DevToolsActivePort" ]] ||
     ! /bin/kill -0 "${active_chrome_pid}" >/dev/null 2>&1; then
    echo "The isolated Chrome fixture failed to launch." >&2
    /bin/cat "${chrome_log_path}" >&2
    return 1
  fi

  ORHOM_TEST_CHROME_PID="${active_chrome_pid}" \
    /usr/bin/osascript -l JavaScript - >/dev/null <<'JXA'
ObjC.import("AppKit");
ObjC.import("Foundation");

function run() {
  const pid = Number(
    ObjC.unwrap(
      $.NSProcessInfo.processInfo.environment.objectForKey(
        "ORHOM_TEST_CHROME_PID"
      )
    )
  );
  const application =
    $.NSRunningApplication.runningApplicationWithProcessIdentifier(pid);
  if (!application) {
    throw new Error("The isolated Chrome process is not running.");
  }
  if (!application.activateWithOptions(2)) {
    throw new Error("The isolated Chrome process could not be activated.");
  }
}
JXA
  local frontmost_pid=""
  for _ in {1..100}; do
    frontmost_pid="$(orhom_test_capture_frontmost_pid)"
    if [[ "${frontmost_pid}" == "${active_chrome_pid}" ]]; then
      break
    fi
    /bin/sleep 0.05
  done
  if [[ "${frontmost_pid}" != "${active_chrome_pid}" ]]; then
    echo "The isolated Chrome process did not become frontmost." >&2
    echo "Expected PID: ${active_chrome_pid}; actual PID: ${frontmost_pid}" >&2
    return 1
  fi
  wait_for_web_state \
    "${field_case}" \
    "${initial_value}" \
    "${expected_focus}" >/dev/null
}

run_case() {
  local field_case="$1"
  local expected_result="$2"
  local expected_focus="$3"
  local case_key="${field_case//-/}"
  local initial_value="L${orhom_test_run_prefix}${case_key}MarkerR"
  local diagnostic_token="${orhom_test_run_prefix}${case_key}Token"
  local sentinel="${orhom_test_run_prefix}${case_key}Clipboard"
  local expected_value="${initial_value}"
  local log_segment_path="${test_temp_dir}/${field_case}.log"
  local final_title
  local -a final_fields

  if [[ "${expected_result}" == "insert" ]]; then
    if [[ "${field_case}" == "input-email" ]]; then
      # The HTML selection APIs intentionally do not apply to
      # input[type=email], so the fixture's real focused caret is at the end.
      expected_value="${initial_value}${diagnostic_token}"
    else
      expected_value="${initial_value%R}${diagnostic_token}R"
    fi
  fi

  launch_isolated_chrome \
    "${field_case}" \
    "${initial_value}" \
    "${expected_focus}"

  local initial_title
  initial_title="$(chrome_page_title)"
  local -a initial_fields
  initial_fields=("${(@s/:/)initial_title}")
  if ! parse_web_title \
      "${initial_title}" \
      "${field_case}" \
      "${initial_value}" \
      "${expected_focus}" ||
     [[ "${initial_fields[5]}" != "0" ]] ||
     [[ "${initial_fields[6]}" != "0" ]]; then
    echo "Chrome fixture did not start from a pristine state." >&2
    return 1
  fi

  local log_offset
  log_offset="$(orhom_test_log_offset "${log_path}")"
  orhom_test_set_clipboard_text "${sentinel}"
  orhom_test_run_diagnostic \
    "${app_path}" \
    "${diagnostic_token}" \
    "com.google.Chrome" \
    "${active_chrome_pid}" \
    "" \
    "0"

  final_title="$(
    wait_for_web_state \
      "${field_case}" \
      "${expected_value}" \
      "${expected_focus}"
  )"
  final_fields=("${(@s/:/)final_title}")
  if [[ "${expected_result}" == "insert" ]]; then
    if (( final_fields[5] < 1 )); then
      echo "${field_case} changed without an observable input event." >&2
      return 1
    fi
  else
    if [[ "${final_fields[5]}" != "0" ]]; then
      echo "${field_case} emitted an input event despite refusal." >&2
      return 1
    fi
  fi

  if [[ "$(orhom_test_clipboard_text)" != "${sentinel}" ]]; then
    echo "ORhom did not restore the clipboard for ${field_case}." >&2
    return 1
  fi

  orhom_test_write_log_segment \
    "${log_path}" \
    "${log_offset}" \
    "${log_segment_path}"
  if [[ "${expected_result}" == "insert" ]]; then
    orhom_test_assert_positive_log \
      "${log_segment_path}" \
      "com.google.Chrome" \
      "${active_chrome_pid}"
  else
    local refusal_pattern
    local require_no_dispatch=false
    case "${field_case}" in
      input-password)
        refusal_pattern="secure-text-field|target-security-indeterminate|unsafe-target secure-text-field|focus-confirmation-timeout current-web-element-not-editable"
        require_no_dispatch=true
        ;;
      input-readonly)
        refusal_pattern="focused-element-not-editable|focus-confirmation-timeout current-web-element-not-editable|focus-confirmation-timeout focused-element-outside-foreground-window"
        require_no_dispatch=true
        ;;
      input-disabled)
        refusal_pattern="target-missing|focus-confirmation-timeout captured-element-missing|focused-element-not-editable|focus-confirmation-timeout focused-element-outside-foreground-window"
        require_no_dispatch=true
        ;;
    esac
    orhom_test_assert_negative_log \
      "${log_segment_path}" \
      "com.google.Chrome" \
      "${active_chrome_pid}" \
      "${refusal_pattern}" \
      "${require_no_dispatch}"
  fi

  terminate_active_chrome
  /usr/bin/printf '%-22s %-10s %-8s input=%s paste=%s\n' \
    "${field_case}" \
    "PASS" \
    "${expected_result}" \
    "${final_fields[5]}" \
    "${final_fields[6]}"
}

run_omnibox_case() {
  local initial_value="L${orhom_test_run_prefix}OmniboxMarkerR"
  local diagnostic_token="${orhom_test_run_prefix}OmniboxToken"
  local sentinel="${orhom_test_run_prefix}OmniboxClipboard"
  local log_segment_path="${test_temp_dir}/omnibox.log"
  local initial_title
  local focused_state=""
  local focused_role=""
  local focused_subrole=""
  local focused_value=""
  local focused_window_number=""
  local log_offset
  local state_remainder=""

  launch_isolated_chrome \
    "contenteditable" \
    "${initial_value}" \
    "1" \
    "omnibox"
  initial_title="$(chrome_page_title)"

  focused_state="$(
    "${ax_field_probe_binary}" \
      focus-omnibox "${active_chrome_pid}"
  )"
  focused_role="${focused_state%%|*}"
  state_remainder="${focused_state#*|}"
  focused_subrole="${state_remainder%%|*}"
  state_remainder="${state_remainder#*|}"
  focused_window_number="${state_remainder%%|*}"
  focused_value="${state_remainder#*|}"
  if [[ "${focused_role}" != "AXTextField" ]] &&
     [[ "${focused_role}" != "AXSearchField" ]]; then
    echo "Chrome did not expose its omnibox as a focused text field." >&2
    echo "Focused state: ${focused_state}" >&2
    return 1
  fi
  if [[ "${focused_window_number}" != <-> ]]; then
    echo "Chrome did not expose a stable omnibox window number." >&2
    return 1
  fi

  log_offset="$(orhom_test_log_offset "${log_path}")"
  orhom_test_set_clipboard_text "${sentinel}"
  orhom_test_run_diagnostic \
    "${app_path}" \
    "${diagnostic_token}" \
    "com.google.Chrome" \
    "${active_chrome_pid}" \
    "${focused_window_number}" \
    "1"

  for _ in {1..100}; do
    focused_state="$(
      "${ax_field_probe_binary}" \
        read-omnibox "${active_chrome_pid}" 2>/dev/null ||
        true
    )"
    focused_role="${focused_state%%|*}"
    state_remainder="${focused_state#*|}"
    focused_subrole="${state_remainder%%|*}"
    state_remainder="${state_remainder#*|}"
    focused_window_number="${state_remainder%%|*}"
    focused_value="${state_remainder#*|}"
    if [[ "${focused_value}" == "${diagnostic_token}" ]]; then
      break
    fi
    /bin/sleep 0.05
  done
  if [[ "${focused_value}" != "${diagnostic_token}" ]]; then
    echo "The Chrome omnibox did not contain exactly the diagnostic token." >&2
    return 1
  fi
  if [[ "$(chrome_page_title)" != "${initial_title}" ]]; then
    echo "The Chrome omnibox test unexpectedly navigated its fixture tab." >&2
    return 1
  fi
  if [[ "$(orhom_test_clipboard_text)" != "${sentinel}" ]]; then
    echo "ORhom did not restore the clipboard for the Chrome omnibox." >&2
    return 1
  fi

  orhom_test_write_log_segment \
    "${log_path}" \
    "${log_offset}" \
    "${log_segment_path}"
  orhom_test_assert_positive_log \
    "${log_segment_path}" \
    "com.google.Chrome" \
    "${active_chrome_pid}"
  if ! /usr/bin/grep -Fq \
    "Paste completed. Method=AXSelectedText" "${log_segment_path}"; then
    echo "ORhom did not use direct AX insertion for the Chrome omnibox." >&2
    return 1
  fi

  terminate_active_chrome
  /usr/bin/printf '%-22s %-10s %-8s role=%s subrole=%s\n' \
    "omnibox" \
    "PASS" \
    "insert" \
    "${focused_role}" \
    "${focused_subrole:-unknown}"
}

run_selected_case() {
  local field_case="$1"
  local expected_result="$2"
  local expected_focus="$3"
  if [[ -n "${test_case_filter}" ]] &&
     [[ ",${test_case_filter}," != *",${field_case},"* ]]; then
    return
  fi
  run_case "${field_case}" "${expected_result}" "${expected_focus}"
  if [[ "${expected_result}" == "insert" ]]; then
    (( ++insertion_case_count ))
  else
    (( ++refusal_case_count ))
  fi
}

run_selected_omnibox_case() {
  if [[ -n "${test_case_filter}" ]] &&
     [[ ",${test_case_filter}," != *",omnibox,"* ]]; then
    return
  fi
  run_omnibox_case
  (( ++insertion_case_count ))
}

if [[ ! -x "${app_binary}" ]]; then
  echo "Install ORhom.app before running the Chrome field matrix." >&2
  exit 1
fi
if [[ ! -x "${chrome_binary}" ]]; then
  echo "Google Chrome is required for the isolated web field matrix." >&2
  exit 1
fi
if [[ ! -f "${fixture_path}" ]]; then
  echo "The Chrome text-field fixture is missing." >&2
  exit 1
fi
if [[ ! -f "${ax_field_probe_source}" ]]; then
  echo "The Chrome accessibility field probe is missing." >&2
  exit 1
fi
if [[ "${original_frontmost_pid}" != <-> ]]; then
  echo "The originally frontmost application could not be identified." >&2
  exit 1
fi

/usr/bin/xcrun swiftc \
  -warnings-as-errors \
  "${ax_field_probe_source}" \
  -o "${ax_field_probe_binary}" \
  -framework AppKit \
  -framework ApplicationServices

orhom_test_snapshot_clipboard "${clipboard_before_path}"
orhom_test_stop_orhom

/usr/bin/printf '%-22s %-10s %-8s %s\n' \
  "WEB CASE" "RESULT" "EXPECTED" "EVENTS"
/usr/bin/printf '%-22s %-10s %-8s %s\n' \
  "----------------------" "----------" "--------" "-----------------"
run_selected_case input-text insert 1
run_selected_case input-search insert 1
run_selected_case input-email insert 1
run_selected_case input-url insert 1
run_selected_case textarea insert 1
run_selected_case contenteditable insert 1
run_selected_case input-password refuse 1
run_selected_case input-readonly refuse 1
run_selected_case input-disabled refuse 0
run_selected_omnibox_case

restore_outcome="$(
  orhom_test_restore_clipboard_if_owned \
    "${clipboard_before_path}" \
    "${orhom_test_run_prefix}"
)"
if [[ "${restore_outcome}" != "restored" ]]; then
  echo "The clipboard changed externally during the Chrome field matrix." >&2
  exit 1
fi
clipboard_recovered=true
orhom_test_snapshot_clipboard "${clipboard_after_path}"
if ! /usr/bin/cmp -s \
  "${clipboard_before_path}" "${clipboard_after_path}"; then
  echo "The Chrome field matrix did not restore the clipboard exactly." >&2
  exit 1
fi

if [[ "${orhom_was_running}" == true ]]; then
  orhom_test_restart_orhom "${app_path}"
fi
orhom_test_restore_frontmost_pid "${original_frontmost_pid}"
/bin/rm -rf -- "${test_temp_dir}"
trap - EXIT

echo "ORhom Chrome text-field matrix passed (${insertion_case_count} insertion and ${refusal_case_count} refusal cases)."
