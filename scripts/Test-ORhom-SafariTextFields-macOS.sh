#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
source "${script_dir}/lib/ORhomTextFieldTestSupport.zsh"

app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
safari_app_path="/Applications/Safari.app"
fixture_path="${repo_dir}/macos/tests/fixtures/text-field-matrix.html"
ax_window_probe_source="${repo_dir}/macos/tests/fixtures/ORhomAXFieldProbe.swift"
log_path="${HOME}/Library/Logs/ORhom/app.log"
test_temp_dir="$(/usr/bin/mktemp -d -t orhom-safari-field-smoke)"
ax_window_probe_binary="${test_temp_dir}/ORhomAXWindowProbe"
clipboard_before_path="${test_temp_dir}/clipboard-before.json"
clipboard_after_path="${test_temp_dir}/clipboard-after.json"
run_identifier="$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
orhom_test_run_prefix="ORhomSafariSmoke${run_identifier}"
original_frontmost_pid="$(orhom_test_capture_frontmost_pid)"
orhom_was_running=false
safari_started_by_test=false
safari_test_window_id=""
safari_process_pid=""
fixture_server_pid=""
fixture_server_port=""
clipboard_snapshot_taken=false
clipboard_touched=false
clipboard_recovered=false
orhom_state_modified=false

if /usr/bin/pgrep -x ORhom >/dev/null 2>&1; then
  orhom_was_running=true
fi

fixture_base_url=""

stop_fixture_server() {
  if [[ "${fixture_server_pid}" != <-> ]]; then
    return
  fi
  if /bin/kill -0 "${fixture_server_pid}" >/dev/null 2>&1; then
    local server_command
    server_command="$(
      /bin/ps -p "${fixture_server_pid}" -o command= 2>/dev/null ||
        true
    )"
    if [[ "${server_command}" != *"-m http.server"* ]] ||
       [[ "${server_command}" != *"--directory ${repo_dir}"* ]]; then
      echo \
        "Warning: Safari fixture-server PID ownership changed; it was not terminated." \
        >&2
      fixture_server_pid=""
      fixture_server_port=""
      return
    fi
    /bin/kill -TERM "${fixture_server_pid}" >/dev/null 2>&1 || true
    for _ in {1..50}; do
      if ! /bin/kill -0 "${fixture_server_pid}" >/dev/null 2>&1; then
        break
      fi
      /bin/sleep 0.05
    done
  fi
  fixture_server_pid=""
  fixture_server_port=""
}

start_fixture_server() {
  local server_log_path="${test_temp_dir}/fixture-server.log"
  PYTHONUNBUFFERED=1 /usr/bin/python3 \
    -m http.server 0 \
    --bind 127.0.0.1 \
    --directory "${repo_dir}" \
    >"${server_log_path}" 2>&1 &
  fixture_server_pid=$!

  for _ in {1..100}; do
    if ! /bin/kill -0 "${fixture_server_pid}" >/dev/null 2>&1; then
      break
    fi
    fixture_server_port="$(
      /usr/bin/sed -nE \
        's/.* port ([0-9]+) .*/\1/p' \
        "${server_log_path}" |
        /usr/bin/head -n 1
    )"
    if [[ "${fixture_server_port}" == <-> ]]; then
      fixture_base_url="http://127.0.0.1:${fixture_server_port}/macos/tests/fixtures/text-field-matrix.html"
      return
    fi
    /bin/sleep 0.05
  done

  echo "The local Safari fixture server did not start." >&2
  /bin/cat "${server_log_path}" >&2
  return 1
}

safari_process_bundle_identifier() {
  local process_identifier="$1"
  ORHOM_SAFARI_PID="${process_identifier}" \
    /usr/bin/osascript -l JavaScript - <<'JXA' 2>/dev/null
ObjC.import("AppKit");
ObjC.import("Foundation");

function run() {
  const value = ObjC.unwrap(
    $.NSProcessInfo.processInfo.environment.objectForKey(
      "ORHOM_SAFARI_PID"
    )
  );
  const application =
    $.NSRunningApplication.runningApplicationWithProcessIdentifier(
      Number(value)
    );
  if (!application || application.terminated) {
    return "";
  }
  const bundleIdentifier = application.bundleIdentifier;
  return bundleIdentifier ? ObjC.unwrap(bundleIdentifier) : "";
}
JXA
}

capture_unique_safari_pid() {
  /usr/bin/osascript -l JavaScript - <<'JXA' 2>/dev/null
ObjC.import("AppKit");

function run() {
  const applications =
    $.NSWorkspace.sharedWorkspace.runningApplications;
  const matches = [];
  for (let index = 0; index < applications.count; index++) {
    const application = applications.objectAtIndex(index);
    const bundleIdentifier = application.bundleIdentifier;
    if (
      bundleIdentifier &&
      ObjC.unwrap(bundleIdentifier) === "com.apple.Safari" &&
      !application.terminated
    ) {
      matches.push(Number(application.processIdentifier));
    }
  }
  return matches.length === 1 ? String(matches[0]) : "";
}
JXA
}

close_owned_safari_window() {
  if [[ "${safari_started_by_test}" != true ]]; then
    return
  fi
  if [[ "${safari_process_pid}" != <-> ]]; then
    echo \
      "Warning: the Safari test PID is unavailable; Safari was left untouched." \
      >&2
    return
  fi
  if ! /bin/kill -0 "${safari_process_pid}" >/dev/null 2>&1; then
    safari_test_window_id=""
    safari_process_pid=""
    safari_started_by_test=false
    return
  fi
  if [[ "$(safari_process_bundle_identifier \
      "${safari_process_pid}" || true)" != "com.apple.Safari" ]]; then
    echo \
      "Warning: Safari process ownership could not be proven; it was left running." \
      >&2
    return
  fi
  if [[ ! -x "${ax_window_probe_binary}" ]]; then
    echo \
      "Warning: the Safari window probe is unavailable; Safari was left untouched." \
      >&2
    return
  fi
  if [[ "${safari_test_window_id}" != <-> ]]; then
    echo \
      "Warning: the marked Safari test window is unavailable; Safari was left untouched." \
      >&2
    return
  fi

  local close_outcome=""
  if ! close_outcome="$(
    "${ax_window_probe_binary}" \
      close-window \
      "${safari_process_pid}" \
      "com.apple.Safari" \
      "${safari_test_window_id}" 2>/dev/null
  )" ||
     [[ "${close_outcome}" != "closed" ]]; then
    echo \
      "Warning: the marked Safari test window no longer matched; Safari was left untouched." \
      >&2
    return
  fi
  safari_test_window_id=""

  local window_count=""
  local observed_window_count=""
  for _ in {1..50}; do
    if ! /bin/kill -0 "${safari_process_pid}" >/dev/null 2>&1; then
      safari_process_pid=""
      safari_started_by_test=false
      return
    fi
    observed_window_count="$(
      "${ax_window_probe_binary}" \
        window-count \
        "${safari_process_pid}" \
        "com.apple.Safari" 2>/dev/null || true
    )"
    if [[ "${observed_window_count}" == <-> ]]; then
      window_count="${observed_window_count}"
      if [[ "${window_count}" == "0" ]]; then
        break
      fi
    fi
    /bin/sleep 0.05
  done

  if [[ "${window_count}" != <-> ]]; then
    echo \
      "Warning: Safari's remaining windows could not be verified; Safari was left running." \
      >&2
    safari_process_pid=""
    safari_started_by_test=false
    return
  fi
  if [[ "${window_count}" != "0" ]]; then
    echo \
      "Warning: Safari has another window; only the marked test window was closed." \
      >&2
    safari_process_pid=""
    safari_started_by_test=false
    return
  fi

  ORHOM_SAFARI_PID="${safari_process_pid}" \
    /usr/bin/osascript -l JavaScript - >/dev/null <<'JXA' || true
ObjC.import("AppKit");
ObjC.import("Foundation");

function run() {
  const pid = Number(
    ObjC.unwrap(
      $.NSProcessInfo.processInfo.environment.objectForKey(
        "ORHOM_SAFARI_PID"
      )
    )
  );
  const application =
    $.NSRunningApplication.runningApplicationWithProcessIdentifier(pid);
  if (
    application &&
    ObjC.unwrap(application.bundleIdentifier) === "com.apple.Safari"
  ) {
    application.terminate;
  }
}
JXA
  for _ in {1..100}; do
    if ! /bin/kill -0 "${safari_process_pid}" >/dev/null 2>&1; then
      break
    fi
    /bin/sleep 0.05
  done
  if /bin/kill -0 "${safari_process_pid}" >/dev/null 2>&1; then
    echo \
      "Warning: windowless Safari did not terminate gracefully; it was left running." \
      >&2
  fi

  safari_process_pid=""
  safari_started_by_test=false
}

quiesce_orhom_if_modified() {
  if [[ "${orhom_state_modified}" == true ]]; then
    orhom_test_stop_orhom >/dev/null 2>&1 || true
  fi
}

restore_orhom_state() {
  if [[ "${orhom_state_modified}" != true ]]; then
    return
  fi
  orhom_test_stop_orhom >/dev/null 2>&1 || true
  if [[ "${orhom_was_running}" == true ]]; then
    orhom_test_restart_orhom "${app_path}" >/dev/null 2>&1 || true
  fi
  orhom_state_modified=false
}

cleanup() {
  quiesce_orhom_if_modified
  close_owned_safari_window
  stop_fixture_server
  restore_orhom_state

  if [[ "${clipboard_snapshot_taken}" == true ]] &&
     [[ "${clipboard_touched}" == true ]] &&
     [[ "${clipboard_recovered}" != true ]]; then
    local restore_outcome
    restore_outcome="$(
      orhom_test_restore_clipboard_if_owned \
        "${clipboard_before_path}" \
        "${orhom_test_run_prefix}" 2>/dev/null || true
    )"
    if [[ "${restore_outcome}" != "restored" ]]; then
      echo \
        "Warning: the original clipboard could not be recovered safely." \
        >&2
    fi
  fi

  orhom_test_restore_frontmost_pid "${original_frontmost_pid}"
  /bin/rm -rf -- "${test_temp_dir}"
}
trap cleanup EXIT

safari_page_title() {
  if [[ "${safari_process_pid}" != <-> ]] ||
     [[ "${safari_test_window_id}" != <-> ]]; then
    return 1
  fi

  "${ax_window_probe_binary}" \
    read-window \
    "${safari_process_pid}" \
    "com.apple.Safari" \
    "${safari_test_window_id}" 2>/dev/null
}

parse_web_title() {
  local title="$1"
  local expected_case="$2"
  local expected_value="$3"
  local expected_focus="$4"
  local expected_prefix
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
    title="$(safari_page_title 2>/dev/null || true)"
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
  echo "Safari fixture did not publish the expected state." >&2
  echo "Last page title: ${title}" >&2
  return 1
}

find_safari_window_by_title_prefix() {
  local title_prefix="$1"
  if [[ "${safari_process_pid}" != <-> ]]; then
    return 1
  fi

  "${ax_window_probe_binary}" \
    focus-window \
    "${safari_process_pid}" \
    "com.apple.Safari" \
    "${title_prefix}" 2>/dev/null
}

open_safari_fixture() {
  local fixture_url="$1"
  local title_prefix="$2"
  local window_record=""
  local record_window_id=""
  local record_title=""
  local frontmost_pid=""

  if [[ "${safari_started_by_test}" != true ]]; then
    if /usr/bin/pgrep -x Safari >/dev/null 2>&1 ||
       [[ -n "$(capture_unique_safari_pid || true)" ]]; then
      echo \
        "Safari started after the safety check; the test will not touch it." \
        >&2
      return 1
    fi
    /usr/bin/open -a Safari "${fixture_url}"
    safari_started_by_test=true
    for _ in {1..200}; do
      safari_process_pid="$(capture_unique_safari_pid || true)"
      if [[ "${safari_process_pid}" == <-> ]] &&
         [[ "$(safari_process_bundle_identifier \
            "${safari_process_pid}" || true)" == "com.apple.Safari" ]]; then
        break
      fi
      safari_process_pid=""
      /bin/sleep 0.05
    done
    if [[ "${safari_process_pid}" != <-> ]]; then
      echo "Safari did not expose the process started by this test." >&2
      return 1
    fi
  else
    if [[ "$(safari_process_bundle_identifier \
        "${safari_process_pid}" || true)" != "com.apple.Safari" ]]; then
      echo "The Safari process started by this test is no longer available." >&2
      return 1
    fi
    /usr/bin/open -a Safari "${fixture_url}"
  fi

  for _ in {1..200}; do
    window_record="$(
      find_safari_window_by_title_prefix "${title_prefix}" || true
    )"
    record_window_id="${window_record%%$'\t'*}"
    record_title="${window_record#*$'\t'}"
    if [[ "${record_window_id}" == <-> ]] &&
       [[ "${record_title}" == ORHOM_WEB:* ]]; then
      safari_test_window_id="${record_window_id}"
      break
    fi
    /bin/sleep 0.05
  done
  if [[ "${safari_test_window_id}" != <-> ]]; then
    echo "Safari did not expose the uniquely marked fixture window." >&2
    return 1
  fi

  for _ in {1..100}; do
    frontmost_pid="$(orhom_test_capture_frontmost_pid)"
    if [[ "${frontmost_pid}" == "${safari_process_pid}" ]]; then
      return
    fi
    /bin/sleep 0.05
  done
  echo "The owned Safari fixture window did not become frontmost." >&2
  echo \
    "Expected PID: ${safari_process_pid}; actual PID: ${frontmost_pid}" \
    >&2
  return 1
}

run_case() {
  local field_case="$1"
  local expected_result="$2"
  local case_key="${field_case//-/}"
  local initial_value="L${orhom_test_run_prefix}${case_key}MarkerR"
  local diagnostic_token="${orhom_test_run_prefix}${case_key}Token"
  local sentinel="${orhom_test_run_prefix}${case_key}Clipboard"
  local expected_value="${initial_value}"
  local fixture_url="${fixture_base_url}?case=${field_case}&initial=${initial_value}&run=${orhom_test_run_prefix}"
  local initial_title_prefix="ORHOM_WEB:${field_case}:${initial_value}:1:"
  local log_segment_path="${test_temp_dir}/${field_case}.log"
  local initial_title
  local final_title
  local log_offset
  local -a initial_fields
  local -a final_fields

  if [[ "${expected_result}" == "insert" ]]; then
    expected_value="${initial_value%R}${diagnostic_token}R"
  fi

  safari_test_window_id=""
  open_safari_fixture "${fixture_url}" "${initial_title_prefix}"
  initial_title="$(
    wait_for_web_state \
      "${field_case}" \
      "${initial_value}" \
      "1"
  )"
  initial_fields=("${(@s/:/)initial_title}")
  if [[ "${initial_fields[5]}" != "0" ]] ||
     [[ "${initial_fields[6]}" != "0" ]]; then
    echo "Safari fixture did not start from a pristine state." >&2
    return 1
  fi

  log_offset="$(orhom_test_log_offset "${log_path}")"
  clipboard_touched=true
  orhom_test_set_clipboard_text "${sentinel}"
  orhom_test_run_diagnostic \
    "${app_path}" \
    "${diagnostic_token}" \
    "com.apple.Safari" \
    "${safari_process_pid}" \
    "${safari_test_window_id}"

  final_title="$(
    wait_for_web_state \
      "${field_case}" \
      "${expected_value}" \
      "1"
  )"
  final_fields=("${(@s/:/)final_title}")
  if [[ "${expected_result}" == "insert" ]]; then
    if (( final_fields[5] < 1 )); then
      echo "${field_case} changed without an observable input event." >&2
      return 1
    fi
  elif [[ "${final_fields[5]}" != "0" ]] ||
       [[ "${final_fields[6]}" != "0" ]]; then
    echo "${field_case} emitted an event despite secure-field refusal." >&2
    return 1
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
      "com.apple.Safari" \
      "${safari_process_pid}"
  else
    if ! /usr/bin/grep -Fq \
      "Bundle='com.apple.Safari'" "${log_segment_path}"; then
      echo "ORhom did not capture Safari for ${field_case}." >&2
      return 1
    fi
    orhom_test_assert_negative_log \
      "${log_segment_path}" \
      "com.apple.Safari" \
      "${safari_process_pid}" \
      "secure-text-field|target-security-indeterminate|unsafe-target secure-text-field" \
      true
    if ! /usr/bin/grep -Fq \
      "Paste diagnostic finished. Settled=true." \
      "${log_segment_path}"; then
      echo "ORhom did not settle the refusal for ${field_case}." >&2
      return 1
    fi
  fi

  /usr/bin/printf '%-18s %-10s %-8s input=%s paste=%s\n' \
    "${field_case}" \
    "PASS" \
    "${expected_result}" \
    "${final_fields[5]}" \
    "${final_fields[6]}"
}

if /usr/bin/pgrep -x Safari >/dev/null 2>&1 ||
   [[ -n "$(capture_unique_safari_pid || true)" ]]; then
  echo \
    "Safari is already running. Close Safari before this isolated test; no Safari window was touched." \
    >&2
  exit 1
fi
if [[ ! -x "${app_binary}" ]]; then
  echo "Install ORhom.app before running the Safari field smoke test." >&2
  exit 1
fi
if [[ ! -d "${safari_app_path}" ]]; then
  echo "Safari is required for the Safari field smoke test." >&2
  exit 1
fi
if [[ ! -f "${fixture_path}" ]]; then
  echo "The Safari text-field fixture is missing." >&2
  exit 1
fi
if [[ ! -f "${ax_window_probe_source}" ]]; then
  echo "The Safari accessibility window probe is missing." >&2
  exit 1
fi
if [[ "${original_frontmost_pid}" != <-> ]]; then
  echo "The originally frontmost application could not be identified." >&2
  exit 1
fi

/usr/bin/xcrun swiftc \
  -warnings-as-errors \
  "${ax_window_probe_source}" \
  -o "${ax_window_probe_binary}" \
  -framework AppKit \
  -framework ApplicationServices

start_fixture_server
orhom_test_snapshot_clipboard "${clipboard_before_path}"
clipboard_snapshot_taken=true
orhom_state_modified=true
orhom_test_stop_orhom

/usr/bin/printf '%-18s %-10s %-8s %s\n' \
  "SAFARI CASE" "RESULT" "EXPECTED" "EVENTS"
/usr/bin/printf '%-18s %-10s %-8s %s\n' \
  "------------------" "----------" "--------" "-----------------"
run_case input-text insert
run_case textarea insert
run_case contenteditable insert
run_case input-password refuse

restore_outcome="$(
  orhom_test_restore_clipboard_if_owned \
    "${clipboard_before_path}" \
    "${orhom_test_run_prefix}"
)"
if [[ "${restore_outcome}" != "restored" ]]; then
  echo "The clipboard changed externally during the Safari field smoke test." >&2
  exit 1
fi
clipboard_recovered=true
orhom_test_snapshot_clipboard "${clipboard_after_path}"
if ! /usr/bin/cmp -s \
  "${clipboard_before_path}" "${clipboard_after_path}"; then
  echo "The Safari field smoke test did not restore the clipboard exactly." >&2
  exit 1
fi

quiesce_orhom_if_modified
close_owned_safari_window
stop_fixture_server
restore_orhom_state
orhom_test_restore_frontmost_pid "${original_frontmost_pid}"
/bin/rm -rf -- "${test_temp_dir}"
trap - EXIT

echo "ORhom Safari field smoke passed (3 insertion and 1 refusal case)."
