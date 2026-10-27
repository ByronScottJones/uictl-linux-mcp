-include tasks/Makefile.*

.DEFAULT_GOAL := help

# Overridable on the command line, e.g. `make build CONFIGURATION=Debug`.
CONFIGURATION ?= Release
SLN ?= uictl.slnx
TEST_PROJECT ?= tests/UICtl.Core.Tests/UICtl.Core.Tests.csproj
CLI_PROJECT ?= src/UICtl.Cli/UICtl.Cli.csproj
GUI_PROJECT ?= src/UICtl.Gui/UICtl.Gui.csproj
SCREENCAPTURE_PROJECT ?= src/UICtl.ScreenCapture/UICtl.ScreenCapture.csproj
# Projects that ship; test projects are deliberately excluded.
SHIPPED_PROJECTS ?= $(CLI_PROJECT) $(SCREENCAPTURE_PROJECT)
SBOM_DIR ?= artifacts/sbom
PUBLISH_DIR ?= artifacts/publish
PREFIX ?= $(HOME)/.local

.PHONY: help check/dotnet deps build test publish install all clean

## Restore NuGet packages
deps: check/dotnet
	dotnet restore $(SLN)

## Build everything and embed the SBOM and library list in each binary
build: deps
	@mkdir -p $(SBOM_DIR)
	@scripts/libs-version.sh $(SHIPPED_PROJECTS) > $(SBOM_DIR)/libs-version.txt
	@scripts/sbom.sh --project $(CLI_PROJECT) --out $(SBOM_DIR) --skip-if-missing
	dotnet build $(SLN) -c $(CONFIGURATION) --no-restore -p:SelfReportDir=$(abspath $(SBOM_DIR))/
	@printf "\nBuilt artifacts:\n"
	@printf "  %s\n" "$(CURDIR)/src/UICtl.Cli/bin/$(CONFIGURATION)/net10.0/uictl" \
		"$(CURDIR)/src/UICtl.Gui/bin/$(CONFIGURATION)/net10.0/uictl-gui" \
		"$(CURDIR)/src/UICtl.ScreenCapture/bin/$(CONFIGURATION)/net10.0/uictl-screencapture"
	@printf "\nRun it:  %s --help\n" "$(CURDIR)/src/UICtl.Cli/bin/$(CONFIGURATION)/net10.0/uictl"

## Run the unit tests
test: deps
	dotnet test $(TEST_PROJECT) -c $(CONFIGURATION) --no-restore

## Publish all three executables to PUBLISH_DIR, with bom.json alongside
publish: build
	dotnet publish $(CLI_PROJECT) -c $(CONFIGURATION) --no-restore -o $(PUBLISH_DIR) -p:SelfReportDir=$(abspath $(SBOM_DIR))/
	dotnet publish $(GUI_PROJECT) -c $(CONFIGURATION) --no-restore -o $(PUBLISH_DIR) -p:SelfReportDir=$(abspath $(SBOM_DIR))/
	dotnet publish $(SCREENCAPTURE_PROJECT) -c $(CONFIGURATION) --no-restore -o $(PUBLISH_DIR) -p:SelfReportDir=$(abspath $(SBOM_DIR))/
	@scripts/sbom.sh --project $(CLI_PROJECT) --out $(PUBLISH_DIR) --skip-if-missing
	@printf "\nPublished to: %s\n" "$(abspath $(PUBLISH_DIR))"

## Install uictl, uictl-gui and uictl-screencapture under PREFIX (default ~/.local)
install: publish
	@scripts/install.sh $(PUBLISH_DIR) $(PREFIX)
	@printf "\nRun it:  %s --help\n" "$(PREFIX)/bin/uictl"

## Build and test
all: build test

## Remove build output, the SBOM and published artifacts
clean: check/dotnet
	dotnet clean $(SLN) -c $(CONFIGURATION)
	rm -rf $(SBOM_DIR) $(PUBLISH_DIR)

check/dotnet:
	@command -v dotnet >/dev/null 2>&1 || { echo "dotnet SDK not found - install .NET 10 (see scripts/setup-ubuntu.sh)"; exit 1; }

## This help screen
help:
	@printf "Available targets:\n\n"
	@awk '/^[a-zA-Z\-_0-9%:\\]+/ { \
		helpMessage = match(lastLine, /^## (.*)/); \
		if (helpMessage) { \
			helpCommand = $$1; \
			helpMessage = substr(lastLine, RSTART + 3, RLENGTH); \
			gsub("\\\\", "", helpCommand); \
			gsub(":+$$", "", helpCommand); \
			printf "  \x1b[32;01m%-35s\x1b[0m %s\n", helpCommand, helpMessage; \
		} \
	} \
	{ lastLine = $$0 }' $(MAKEFILE_LIST) | sort -u
	@printf "\n"
