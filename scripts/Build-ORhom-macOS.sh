#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
build_dir="${repo_dir}/build/macos"
app_path="${build_dir}/ORhom.app"
contents_path="${app_path}/Contents"
module_cache_path="${build_dir}/ModuleCache"
dependency_dir="${repo_dir}/build/dependencies"
whisper_archive="${dependency_dir}/whisper-v1.9.1-xcframework.zip"
whisper_archive_sha256="8c3ecbe73f48b0cb9318fc3058264f951ab336fd530e82c4ccdd2298d1311a4c"
whisper_download_url="https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-v1.9.1-xcframework.zip"
whisper_extract_dir="${dependency_dir}/whisper-v1.9.1"
whisper_framework_source="${whisper_extract_dir}/build-apple/whisper.xcframework/macos-arm64_x86_64/whisper.framework"
codesign_identity="${ORHOM_CODESIGN_IDENTITY:--}"
compatible_sdk="/Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk"
if [[ -d "${compatible_sdk}" ]]; then
  macos_sdk="${compatible_sdk}"
else
  macos_sdk="$(/usr/bin/xcrun --sdk macosx --show-sdk-path)"
fi

/bin/mkdir -p \
  "${contents_path}/MacOS" \
  "${contents_path}/Resources" \
  "${contents_path}/Frameworks" \
  "${module_cache_path}" \
  "${dependency_dir}"
/bin/cp "${repo_dir}/macos/Info.plist" "${contents_path}/Info.plist"

if [[ ! -f "${whisper_archive}" ]]; then
  /usr/bin/curl -fL --retry 3 "${whisper_download_url}" -o "${whisper_archive}"
fi

actual_whisper_sha256="$(/usr/bin/shasum -a 256 "${whisper_archive}" | /usr/bin/awk '{ print $1 }')"
if [[ "${actual_whisper_sha256}" != "${whisper_archive_sha256}" ]]; then
  echo "whisper.cpp XCFramework has an unexpected SHA-256 checksum." >&2
  exit 1
fi

if [[ ! -d "${whisper_framework_source}" ]]; then
  /usr/bin/unzip -oq "${whisper_archive}" -d "${whisper_extract_dir}"
fi

/usr/bin/ditto "${whisper_framework_source}" "${contents_path}/Frameworks/whisper.framework"

/usr/bin/xcrun swiftc \
  -swift-version 5 \
  -warnings-as-errors \
  -parse-as-library \
  -sdk "${macos_sdk}" \
  -target arm64-apple-macosx13.3 \
  -module-cache-path "${module_cache_path}" \
  "${repo_dir}/macos/ORhomMacPolicies.swift" \
  "${repo_dir}/macos/ORhomAudioDucking.swift" \
  "${repo_dir}/macos/ORhomHistory.swift" \
  "${repo_dir}/macos/ORhomInfrastructure.swift" \
  "${repo_dir}/macos/ORhomUI.swift" \
  "${repo_dir}/macos/ORhomMac.swift" \
  -o "${contents_path}/MacOS/ORhom" \
  -F "${contents_path}/Frameworks" \
  -framework AppKit \
  -framework ApplicationServices \
  -framework AudioToolbox \
  -framework AVFoundation \
  -framework Carbon \
  -framework CoreAudio \
  -framework CryptoKit \
  -framework ServiceManagement \
  -framework SwiftUI \
  -framework whisper \
  -Xlinker -rpath \
  -Xlinker "@executable_path/../Frameworks"

/usr/bin/sips -s format icns "${repo_dir}/Assets/ORhom.ico" \
  --out "${contents_path}/Resources/ORhom.icns" >/dev/null

/usr/bin/plutil -lint "${contents_path}/Info.plist" >/dev/null
framework_codesign_options=(--force --sign "${codesign_identity}")
app_codesign_options=(--force --sign "${codesign_identity}")
if [[ "${codesign_identity}" != "-" ]]; then
  framework_codesign_options+=(--options runtime --timestamp)
  app_codesign_options+=(--options runtime --timestamp)
else
  app_identifier="$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "${contents_path}/Info.plist")"
  app_codesign_options+=(
    --requirements
    "=designated => identifier \"${app_identifier}\""
  )
fi
/usr/bin/codesign "${framework_codesign_options[@]}" "${contents_path}/Frameworks/whisper.framework"
/usr/bin/codesign "${app_codesign_options[@]}" "${app_path}"
/usr/bin/codesign --verify --deep --strict "${app_path}"

echo "${app_path}"
