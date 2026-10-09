#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"

echo "Running isolated AppKit text-field matrix..."
"${script_dir}/Test-ORhom-NativeTextFields-macOS.sh"

echo
echo "Running isolated Chrome text-field matrix..."
"${script_dir}/Test-ORhom-ChromeTextFields-macOS.sh"

echo
echo "ORhom text-field matrix passed: 12 writable fields inserted automatically; 7 protected fields remained unchanged."
