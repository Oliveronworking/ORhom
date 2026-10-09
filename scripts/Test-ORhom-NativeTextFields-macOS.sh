#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
source "${script_dir}/lib/ORhomTextFieldTestSupport.zsh"

app_path="/Applications/ORhom.app"
app_binary="${app_path}/Contents/MacOS/ORhom"
log_path="${HOME}/Library/Logs/ORhom/app.log"
fixture_source_dir="${repo_dir}/macos/tests/fixtures/NativeTextFields"
fixture_build_dir="${repo_dir}/build/macos-test-fixtures"
fixture_app="${fixture_build_dir}/ORhomNativeFieldFixture.app"
fixture_binary="${fixture_app}/Contents/MacOS/ORhomNativeFieldFixture"
fixture_bundle_identifier="at.orhom.tests.native-text-fields"
test_temp_dir="$(/usr/bin/mktemp -d -t orhom-native-field-matrix)"
clipboard_before_path="${test_temp_dir}/clipboard-before.json"
clipboard_after_path="${test_temp_dir}/clipboard-after.json"
run_identifier="$(/usr/bin/uuidgen | /usr/bin/tr -d '-')"
orhom_test_run_prefix="ORhomNativeMatrix${run_identifier}"
original_frontmost_pid="$(orhom_test_capture_frontmost_pid)"
active_fixture_pid=""
clipboard_recovered=false

terminate_active_fixture() {
  if [[ "${active_fixture_pid}" != <-> ]]; then
    active_fixture_pid=""
    return
  fi
  if /bin/kill -0 "${active_fixture_pid}" >/dev/null 2>&1; then
    /bin/kill -TERM "${active_fixture_pid}" >/dev/null 2>&1 || true
    for _ in {1..60}; do
      if ! /bin/kill -0 "${active_fixture_pid}" >/dev/null 2>&1; then
        break
      fi
      /bin/sleep 0.05
    done
  fi
  active_fixture_pid=""
}

cleanup() {
  terminate_active_fixture
  orhom_test_stop_orhom >/dev/null 2>&1 || true
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
  orhom_test_restart_orhom "${app_path}" >/dev/null 2>&1 || true
  orhom_test_restore_frontmost_pid "${original_frontmost_pid}"
  /bin/rm -rf -- "${test_temp_dir}"
}
trap cleanup EXIT

state_value() {
  local state_path="$1"
  local key="$2"
  /usr/bin/plutil -extract "${key}" raw -o - "${state_path}"
}

wait_for_fixture_ready() {
  local state_path="$1"
  local expected_case="$2"
  local expected_value="$3"
  local expected_focus="$4"
  for _ in {1..160}; do
    if [[ -f "${state_path}" ]] &&
       [[ "$(state_value "${state_path}" ready 2>/dev/null || true)" == "true" ]] &&
       [[ "$(state_value "${state_path}" caseName 2>/dev/null || true)" == "${expected_case}" ]] &&
       [[ "$(state_value "${state_path}" value 2>/dev/null || true)" == "${expected_value}" ]] &&
       [[ "$(state_value "${state_path}" focused 2>/dev/null || true)" == "${expected_focus}" ]]; then
      return
    fi
    /bin/sleep 0.05
  done
  echo "Native fixture did not become ready for ${expected_case}." >&2
  if [[ -f "${state_path}" ]]; then
    /bin/cat "${state_path}" >&2
  fi
  return 1
}

wait_for_fixture_value() {
  local state_path="$1"
  local expected_value="$2"
  for _ in {1..120}; do
    if [[ -f "${state_path}" ]] &&
       [[ "$(state_value "${state_path}" value 2>/dev/null || true)" == "${expected_value}" ]]; then
      return
    fi
    /bin/sleep 0.05
  done
  echo "Native fixture did not report the exact expected value." >&2
  return 1
}

build_fixture() {
  local macos_sdk
  macos_sdk="$(/usr/bin/xcrun --sdk macosx --show-sdk-path)"
  local target_architecture
  target_architecture="$(/usr/bin/uname -m)"
  local module_cache_path="${fixture_build_dir}/ModuleCache"

  /bin/mkdir -p \
    "${fixture_app}/Contents/MacOS" \
    "${fixture_app}/Contents/Resources" \
    "${module_cache_path}"
  /bin/cp \
    "${fixture_source_dir}/Info.plist" \
    "${fixture_app}/Contents/Info.plist"
  /usr/bin/xcrun swiftc \
    -swift-version 5 \
    -warnings-as-errors \
    -sdk "${macos_sdk}" \
    -target "${target_architecture}-apple-macosx13.3" \
    -module-cache-path "${module_cache_path}" \
    "${fixture_source_dir}/main.swift" \
    -framework AppKit \
    -framework SwiftUI \
    -o "${fixture_binary}"
  /usr/bin/plutil -lint \
    "${fixture_app}/Contents/Info.plist" >/dev/null
  /usr/bin/codesign \
    --force \
    --sign - \
    --requirements \
    '=designated => identifier "at.orhom.tests.native-text-fields"' \
    "${fixture_app}" >/dev/null
  /usr/bin/codesign --verify --deep --strict "${fixture_app}"
}

launch_fixture() {
  local field_case="$1"
  local initial_value="$2"
  local state_path="$3"
  local expected_focus="$4"

  /usr/bin/open -n -a "${fixture_app}" \
    --args \
    --case "${field_case}" \
    --initial-text "${initial_value}" \
    --state-path "${state_path}"

  for _ in {1..120}; do
    if [[ -f "${state_path}" ]]; then
      active_fixture_pid="$(
        state_value "${state_path}" processIdentifier 2>/dev/null || true
      )"
      if [[ "${active_fixture_pid}" == <-> ]]; then
        break
      fi
    fi
    /bin/sleep 0.05
  done
  if [[ "${active_fixture_pid}" != <-> ]]; then
    echo "Native fixture did not publish a valid process identifier." >&2
    return 1
  fi

  ORHOM_TEST_FIXTURE_PID="${active_fixture_pid}" \
    /usr/bin/osascript >/dev/null <<'APPLESCRIPT'
set fixturePID to (system attribute "ORHOM_TEST_FIXTURE_PID") as integer
tell application "System Events"
  set matches to every application process whose unix id is fixturePID
  if (count matches) is not 1 then
    error "The isolated native fixture process is not running."
  end if
  set frontmost of item 1 of matches to true
end tell
APPLESCRIPT
  wait_for_fixture_ready \
    "${state_path}" \
    "${field_case}" \
    "${initial_value}" \
    "${expected_focus}"
}

assert_native_metadata() {
  local state_path="$1"
  local field_case="$2"
  case "${field_case}" in
    text-field|search-field|text-view|combo-box|swiftui-text-field|swiftui-text-editor)
      [[ "$(state_value "${state_path}" editable)" == "true" ]]
      [[ "$(state_value "${state_path}" enabled)" == "true" ]]
      [[ "$(state_value "${state_path}" shouldAcceptAutomaticInsertion)" == "true" ]]
      ;;
    secure-field|swiftui-secure-field)
      [[ "$(state_value "${state_path}" secure)" == "true" ]]
      [[ "$(state_value "${state_path}" editable)" == "true" ]]
      [[ "$(state_value "${state_path}" enabled)" == "true" ]]
      [[ "$(state_value "${state_path}" shouldAcceptAutomaticInsertion)" == "false" ]]
      ;;
    readonly-field)
      [[ "$(state_value "${state_path}" editable)" == "false" ]]
      [[ "$(state_value "${state_path}" enabled)" == "true" ]]
      [[ "$(state_value "${state_path}" shouldAcceptAutomaticInsertion)" == "false" ]]
      ;;
    disabled-field)
      [[ "$(state_value "${state_path}" enabled)" == "false" ]]
      [[ "$(state_value "${state_path}" shouldAcceptAutomaticInsertion)" == "false" ]]
      ;;
  esac
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
  local state_path="${test_temp_dir}/${field_case}.json"
  local log_segment_path="${test_temp_dir}/${field_case}.log"

  if [[ "${expected_result}" == "insert" ]]; then
    expected_value="${initial_value%R}${diagnostic_token}R"
  fi

  launch_fixture \
    "${field_case}" \
    "${initial_value}" \
    "${state_path}" \
    "${expected_focus}"
  assert_native_metadata "${state_path}" "${field_case}"

  local log_offset
  log_offset="$(orhom_test_log_offset "${log_path}")"
  orhom_test_set_clipboard_text "${sentinel}"
  orhom_test_run_diagnostic \
    "${app_path}" \
    "${diagnostic_token}" \
    "${fixture_bundle_identifier}" \
    "${active_fixture_pid}"

  if [[ "${expected_result}" == "insert" ]]; then
    wait_for_fixture_value "${state_path}" "${expected_value}"
  else
    /bin/sleep 0.30
    if [[ "$(state_value "${state_path}" value)" != "${initial_value}" ]]; then
      echo "${field_case} changed although insertion must be refused." >&2
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
      "${fixture_bundle_identifier}" \
      "${active_fixture_pid}"
  else
    local refusal_pattern
    case "${field_case}" in
      secure-field|swiftui-secure-field)
        refusal_pattern="secure-text-field|target-security-indeterminate|unsafe-target secure-text-field"
        ;;
      readonly-field|disabled-field)
        refusal_pattern="focus-confirmation-timeout captured-element-missing|focused-element-not-editable|target-missing"
        ;;
    esac
    orhom_test_assert_negative_log \
      "${log_segment_path}" \
      "${fixture_bundle_identifier}" \
      "${active_fixture_pid}" \
      "${refusal_pattern}" \
      true
  fi

  terminate_active_fixture
  /usr/bin/printf '%-22s %-10s %s\n' \
    "${field_case}" \
    "PASS" \
    "${expected_result}"
}

if [[ ! -x "${app_binary}" ]]; then
  echo "Install ORhom.app before running the native field matrix." >&2
  exit 1
fi
if [[ "${original_frontmost_pid}" != <-> ]]; then
  echo "The originally frontmost application could not be identified." >&2
  exit 1
fi

orhom_test_snapshot_clipboard "${clipboard_before_path}"
build_fixture
orhom_test_stop_orhom

/usr/bin/printf '%-22s %-10s %s\n' "NATIVE CASE" "RESULT" "EXPECTATION"
/usr/bin/printf '%-22s %-10s %s\n' "----------------------" "----------" "-----------"
run_case text-field insert true
run_case search-field insert true
run_case text-view insert true
run_case combo-box insert true
run_case secure-field refuse true
run_case readonly-field refuse false
run_case disabled-field refuse false
run_case swiftui-text-field insert true
run_case swiftui-text-editor insert true
run_case swiftui-secure-field refuse true

restore_outcome="$(
  orhom_test_restore_clipboard_if_owned \
    "${clipboard_before_path}" \
    "${orhom_test_run_prefix}"
)"
if [[ "${restore_outcome}" != "restored" ]]; then
  echo "The clipboard changed externally during the native field matrix." >&2
  exit 1
fi
clipboard_recovered=true
orhom_test_snapshot_clipboard "${clipboard_after_path}"
if ! /usr/bin/cmp -s \
  "${clipboard_before_path}" "${clipboard_after_path}"; then
  echo "The native field matrix did not restore the clipboard exactly." >&2
  exit 1
fi

orhom_test_restart_orhom "${app_path}"
orhom_test_restore_frontmost_pid "${original_frontmost_pid}"
/bin/rm -rf -- "${test_temp_dir}"
trap - EXIT

echo "ORhom native text-field matrix passed (6 insertion and 4 refusal cases)."
