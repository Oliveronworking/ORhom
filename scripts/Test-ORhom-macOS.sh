#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
test_build_dir="${repo_dir}/build/macos-tests"
test_binary="${test_build_dir}/ORhomMacTests"
module_cache_path="${test_build_dir}/ModuleCache"
macos_sdk="$(/usr/bin/xcrun --sdk macosx --show-sdk-path)"

/bin/mkdir -p "${test_build_dir}" "${module_cache_path}"

/usr/bin/xcrun swiftc \
  -swift-version 5 \
  -warnings-as-errors \
  -sdk "${macos_sdk}" \
  -target arm64-apple-macosx13.3 \
  -module-cache-path "${module_cache_path}" \
  "${repo_dir}/macos/ORhomMacPolicies.swift" \
  "${repo_dir}/macos/ORhomHotKeyGesture.swift" \
  "${repo_dir}/macos/ORhomAudioProcessing.swift" \
  "${repo_dir}/macos/ORhomAudioProcessingTests.swift" \
  "${repo_dir}/macos/ORhomDictationText.swift" \
  "${repo_dir}/macos/ORhomDictationTextTests.swift" \
  "${repo_dir}/macos/ORhomPasteboard.swift" \
  "${repo_dir}/macos/ORhomAudioDucking.swift" \
  "${repo_dir}/macos/ORhomHistory.swift" \
  "${repo_dir}/macos/ORhomMacTests.swift" \
  -framework AppKit \
  -framework AudioToolbox \
  -framework Carbon \
  -framework CoreAudio \
  -o "${test_binary}"

"${test_binary}"
