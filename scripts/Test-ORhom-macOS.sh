#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
test_build_dir="${repo_dir}/build/macos-tests"
test_binary="${test_build_dir}/ORhomMacTests"
compatible_sdk="/Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk"
if [[ -d "${compatible_sdk}" ]]; then
  macos_sdk="${compatible_sdk}"
else
  macos_sdk="$(/usr/bin/xcrun --sdk macosx --show-sdk-path)"
fi

/bin/mkdir -p "${test_build_dir}"

/usr/bin/xcrun swiftc \
  -swift-version 5 \
  -warnings-as-errors \
  -sdk "${macos_sdk}" \
  -target arm64-apple-macosx13.3 \
  "${repo_dir}/macos/ORhomMacPolicies.swift" \
  "${repo_dir}/macos/ORhomAudioDucking.swift" \
  "${repo_dir}/macos/ORhomHistory.swift" \
  "${repo_dir}/macos/ORhomMacTests.swift" \
  -framework AppKit \
  -framework AudioToolbox \
  -framework Carbon \
  -framework CoreAudio \
  -o "${test_binary}"

"${test_binary}"
