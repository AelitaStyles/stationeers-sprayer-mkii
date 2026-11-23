#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG="Release"
SKIP_BUILD=false
MODS_DIR="${STATIONEERS_MODS_DIR:-}"

usage() {
	cat <<EOF
Usage: $(basename "$0") [options]

Builds the mod and deploys it to the local Stationeers mods directory.

Options:
	-c, --config <Debug|Release>   Build configuration (default: Release)
	-m, --mods-dir <path>          Override Stationeers mods directory
	    --no-build                 Skip 'dotnet build' and deploy existing build output
	-h, --help                     Show this help message

Environment:
	STATIONEERS_MODS_DIR           Override Stationeers mods directory
EOF
}

while [[ $# -gt 0 ]]; do
	case "$1" in
		-c|--config)
			CONFIG="${2:?Missing value for $1}"
			shift 2
			;;
		-m|--mods-dir)
			MODS_DIR="${2:?Missing value for $1}"
			shift 2
			;;
		--no-build)
			SKIP_BUILD=true
			shift
			;;
		-h|--help)
			usage
			exit 0
			;;
		*)
			echo "Error: Unknown option '$1'" >&2
			usage >&2
			exit 1
			;;
	esac
done

# Extract AssemblyName from SprayerMkII.props
PROPS_FILE="$SCRIPT_DIR/SprayerMkII.props"
if [[ ! -f "$PROPS_FILE" ]]; then
	echo "Error: Could not find SprayerMkII.props in $SCRIPT_DIR" >&2
	exit 1
fi

ASSEMBLY_NAME="$(sed -n 's/.*<AssemblyName>\([^<]*\)<\/AssemblyName>.*/\1/p' "$PROPS_FILE" | head -n 1 | tr -d '\r\n')"
if [[ -z "$ASSEMBLY_NAME" ]]; then
	echo "Error: Could not determine <AssemblyName> from $PROPS_FILE" >&2
	exit 1
fi

# Auto-detect Stationeers local mods directory if not explicitly provided
detect_mods_dir() {
	local candidates=(
		# Linux Steam (Proton)
		"$HOME/.local/share/Steam/steamapps/compatdata/544550/pfx/drive_c/users/steamuser/Documents/My Games/Stationeers/mods"
		"$HOME/.steam/steam/steamapps/compatdata/544550/pfx/drive_c/users/steamuser/Documents/My Games/Stationeers/mods"
		"$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/compatdata/544550/pfx/drive_c/users/steamuser/Documents/My Games/Stationeers/mods"
		# Linux native / XDG
		"$HOME/Documents/My Games/Stationeers/mods"
		"$HOME/.config/unity3d/Rocketwerkz/rocketstation/mods"
	)

	# Windows (Git Bash / MSYS2 / Cygwin)
	if [[ -n "${USERPROFILE:-}" ]]; then
		candidates+=("$USERPROFILE/Documents/My Games/Stationeers/mods")
	fi

	for dir in "${candidates[@]}"; do
		if [[ -d "$dir" ]] || [[ -d "$(dirname "$dir")" ]]; then
			printf '%s' "$dir"
			return 0
		fi
	done

	return 1
}

if [[ -z "$MODS_DIR" ]]; then
	if ! MODS_DIR="$(detect_mods_dir)"; then
		echo "Error: Could not locate the Stationeers mods directory automatically." >&2
		echo "Pass --mods-dir <path> or set STATIONEERS_MODS_DIR in your environment." >&2
		exit 1
	fi
fi

if [[ "$SKIP_BUILD" == false ]]; then
	echo "Building $ASSEMBLY_NAME ($CONFIG)..."
	dotnet build "$SCRIPT_DIR/SprayerMkII.csproj" -c "$CONFIG"
fi

DLL_PATH="$SCRIPT_DIR/bin/$CONFIG/net48/$ASSEMBLY_NAME.dll"
if [[ ! -f "$DLL_PATH" ]]; then
	echo "Error: Built DLL not found at $DLL_PATH" >&2
	exit 1
fi

TARGET_DIR="$MODS_DIR/$ASSEMBLY_NAME"
mkdir -p "$TARGET_DIR"
cp "$DLL_PATH" "$TARGET_DIR/"

if [[ -d "$SCRIPT_DIR/About" ]]; then
	rm -rf "$TARGET_DIR/About"
	cp -r "$SCRIPT_DIR/About" "$TARGET_DIR/About"
fi

if [[ -f "$SCRIPT_DIR/Assets/sprayermkii.assets" ]]; then
	rm -rf "$TARGET_DIR/Assets"
	mkdir -p "$TARGET_DIR/Assets"
	cp "$SCRIPT_DIR/Assets/sprayermkii.assets" "$TARGET_DIR/Assets/"
else
	rm -rf "$TARGET_DIR/Assets"
fi

echo "Deployed $ASSEMBLY_NAME ($CONFIG) to: $TARGET_DIR"
