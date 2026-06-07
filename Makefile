# ModernNoCupboardDecay — local build chain. See BUILD.md for details.
#
# Quick start:
#   make references-managed   # one-time: download Oxide/Rust reference DLLs
#   make build                # type-check the plugin against them
#
# On Windows, use tools/fetch-references.ps1 + `dotnet build` directly.

SHELL  := /bin/bash
CSPROJ := build/ModernNoCupboardDecay.csproj
CONFIG ?= Release

.DEFAULT_GOAL := build
.PHONY: all build references references-managed clean rebuild help

all: build ## Alias for build

help: ## List available targets
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN{FS=":.*?## "}{printf "  %-20s %s\n", $$1, $$2}'

references: ## Download the full Rust server + Oxide reference assemblies
	tools/fetch-references.sh

references-managed: ## Download references, keeping only the Managed/ folder (small)
	tools/fetch-references.sh --managed-only

build: ## Type-check the plugin against the real Oxide/Rust/Unity assemblies
	dotnet build $(CSPROJ) -c $(CONFIG) --nologo

clean: ## Remove build outputs (keeps downloaded references)
	-dotnet clean $(CSPROJ) -c $(CONFIG) --nologo
	rm -rf build/bin build/obj

rebuild: clean build ## Clean then build
