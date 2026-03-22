#!/bin/bash
set -x
set -euo pipefail
IFS=$'\n\t'

ROOT_DIR=${BUILD_SOURCESDIRECTORY:-$(pwd)}
export TEST_RESULTS_FILE=${TEST_RESULTS_FILE:-$ROOT_DIR/build/skia-linux-headless-xunit-tests-results.xml}

mkdir -p "$(dirname "$TEST_RESULTS_FILE")"

cd "$ROOT_DIR"

dotnet test \
	--project src/Uno.UI.Runtime.Skia.Headless.XUnit.Tests/Uno.UI.Runtime.Skia.Headless.XUnit.Tests.csproj \
	-p:UnoTargetFrameworkOverride=net10.0 \
	--results-directory "$(dirname "$TEST_RESULTS_FILE")" \
	-v minimal \
	-- \
	--report-nunit \
	--report-nunit-filename "$(basename "$TEST_RESULTS_FILE")"
