#!/usr/bin/env bash

set -euo pipefail
umask 077

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(cd "$script_dir/../.." && pwd)"
scene="${1:-ProjectPickerAcceptance.tscn}"
app_name="APP-AGAS-CS"
runner_pid="$$"

if [[ "$scene" == */* || "$scene" != *.tscn || ! -f "$script_dir/$scene" ]]; then
    printf 'Acceptance scene must be an existing tests/Godot/*.tscn file: %s\n' "$scene" >&2
    exit 2
fi

dotnet_bin="$(command -v dotnet || true)"
if [[ -z "$dotnet_bin" ]]; then
    printf 'dotnet was not found on PATH.\n' >&2
    exit 2
fi
dotnet_physical_path="$(python3 - "$dotnet_bin" <<'PY'
import os
import sys
print(os.path.realpath(sys.argv[1]))
PY
)"

godot_bin="${GODOT_MONO_BIN:-}"
if [[ -z "$godot_bin" && -x /Applications/Godot_mono.app/Contents/MacOS/Godot ]]; then
    godot_bin=/Applications/Godot_mono.app/Contents/MacOS/Godot
fi
if [[ -z "$godot_bin" ]]; then
    godot_bin="$(command -v godot-mono || true)"
fi
if [[ -z "$godot_bin" || ! -x "$godot_bin" ]]; then
    printf 'Godot Mono was not found. Set GODOT_MONO_BIN to the executable path.\n' >&2
    exit 2
fi
godot_bin="$(python3 - "$godot_bin" <<'PY'
import os
import sys
print(os.path.realpath(sys.argv[1]))
PY
)"

real_home="${HOME:-}"
if [[ -z "$real_home" ]]; then
    printf 'HOME must be set so real-user paths can be fingerprinted.\n' >&2
    exit 2
fi
real_home="$(cd "$real_home" 2>/dev/null && pwd -P)" || {
    printf 'HOME must identify an existing directory: %s\n' "${HOME:-}" >&2
    exit 2
}
real_agas_home="${AGAS_HOME:-$real_home/.agas}"
real_xdg_config_home="${XDG_CONFIG_HOME:-$real_home/.config}"
real_xdg_data_home="${XDG_DATA_HOME:-$real_home/.local/share}"
real_xdg_cache_home="${XDG_CACHE_HOME:-$real_home/.cache}"
real_nuget_packages="${NUGET_PACKAGES:-$real_home/.nuget/packages}"

dotnet_root="${DOTNET_ROOT:-}"
if [[ -z "$dotnet_root" || ! -x "$dotnet_root/dotnet" || ! -d "$dotnet_root/sdk" ]]; then
    dotnet_command_directory="$(dirname "$dotnet_physical_path")"
    if [[ -x "$dotnet_command_directory/dotnet" && -d "$dotnet_command_directory/sdk" ]]; then
        dotnet_root="$dotnet_command_directory"
    elif [[ -x "$dotnet_command_directory/../libexec/dotnet" && -d "$dotnet_command_directory/../libexec/sdk" ]]; then
        dotnet_root="$(cd "$dotnet_command_directory/../libexec" && pwd -P)"
    else
        printf 'Unable to locate DOTNET_ROOT for %s before isolation.\n' "$dotnet_bin" >&2
        exit 2
    fi
fi

if [[ -d "$real_nuget_packages" ]]; then
    nuget_packages="$(cd "$real_nuget_packages" && pwd -P)"
else
    nuget_packages=""
fi

case "$(uname -s)" in
    Darwin)
        real_godot_app_data="$real_home/Library/Application Support/Godot/app_userdata/$app_name"
        ;;
    *)
        real_godot_app_data="$real_xdg_data_home/godot/app_userdata/$app_name"
        ;;
esac

real_known_paths=(
    "$real_home/.agas"
    "$real_agas_home"
    "$real_home/Documents/AGAS Projects"
    "$real_godot_app_data"
    "$real_xdg_config_home/$app_name"
    "$real_xdg_data_home/$app_name"
    "$real_xdg_cache_home/$app_name"
)

isolation_root="$(mktemp -d "${TMPDIR:-/tmp}/agas-godot-acceptance.XXXXXX")"
snapshot="$isolation_root/project"
fingerprint_before="$isolation_root/real-paths.before.json"
process_group_ready="$isolation_root/process-group.ready"
timeout_marker="$isolation_root/command-timeout"
child_pid=""
timeout_watcher_pid=""
acceptance_lock_dir="/tmp/agas-godot-acceptance-$(id -u).lock"
acceptance_lock_reclaim_dir="${acceptance_lock_dir}.reclaim"
acceptance_lock_acquired=0
acceptance_lock_token=""

command_timeout_seconds="${AGAS_ACCEPTANCE_COMMAND_TIMEOUT_SECONDS:-600}"
if [[ ! "$command_timeout_seconds" =~ ^[1-9][0-9]*$ || "$command_timeout_seconds" -gt 3600 ]]; then
    printf 'AGAS_ACCEPTANCE_COMMAND_TIMEOUT_SECONDS must be an integer from 1 to 3600.\n' >&2
    rm -rf "$isolation_root"
    exit 2
fi

write_fingerprint() {
    local destination="$1"
    local protected_home="$2"
    shift 2
    python3 - "$destination" "$protected_home" "$@" <<'PY'
import hashlib
import json
import os
import stat
import sys

destination, protected_home, *roots = sys.argv[1:]

def describe(path, relative):
    info = os.lstat(path)
    if stat.S_ISREG(info.st_mode): kind = "file"
    elif stat.S_ISDIR(info.st_mode): kind = "directory"
    elif stat.S_ISLNK(info.st_mode): kind = "symlink"
    else: kind = "other"
    item = {
        "relativePath": relative,
        "type": kind,
        "mode": stat.S_IMODE(info.st_mode),
        "device": info.st_dev,
        "inode": info.st_ino,
        "modifiedNanoseconds": info.st_mtime_ns,
    }
    if stat.S_ISREG(info.st_mode):
        digest = hashlib.sha256()
        with open(path, "rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        item["length"] = info.st_size
        item["sha256"] = digest.hexdigest()
    elif stat.S_ISLNK(info.st_mode):
        item["target"] = os.readlink(path)
    return item

def fingerprint(root):
    if not os.path.lexists(root):
        return {"path": root, "exists": False, "entries": []}
    entries = [describe(root, ".")]
    if not os.path.isdir(root) or os.path.islink(root):
        return {"path": root, "exists": True, "entries": entries}
    pending = [(root, "")]
    while pending:
        directory, relative_directory = pending.pop()
        with os.scandir(directory) as iterator:
            children = sorted(iterator, key=lambda entry: entry.name)
        for child in children:
            relative = os.path.join(relative_directory, child.name) if relative_directory else child.name
            entries.append(describe(child.path, relative))
            if child.is_dir(follow_symlinks=False):
                pending.append((child.path, relative))
    entries.sort(key=lambda item: item["relativePath"])
    return {"path": root, "exists": True, "entries": entries}

def ancestors(path):
    current = os.path.abspath(path)
    while True:
        yield current
        parent = os.path.dirname(current)
        if parent == current: break
        current = parent

def same(left, right):
    if os.path.normpath(left) == os.path.normpath(right): return True
    try: return os.path.samefile(left, right)
    except (FileNotFoundError, NotADirectoryError, OSError): return False

def ancestor(candidate, descendant):
    try: return os.path.commonpath([candidate, descendant]) == candidate
    except ValueError: return False

known = [fingerprint(root) for root in roots]
canonical_sources = {}
for root in roots:
    if not os.path.islink(root): continue
    target = os.path.realpath(root)
    broad = ancestor(target, protected_home) or any(same(target, item) for item in ancestors(protected_home))
    if not broad:
        for other in roots:
            if other == root: continue
            other_target = os.path.realpath(other)
            if same(target, other_target): continue
            if ancestor(target, other_target) or any(same(target, item) for item in list(ancestors(other_target))[1:]):
                broad = True
                break
    if broad:
        raise SystemExit(f"known root symlink target is too broad to fingerprint safely: {root} -> {target}")
    canonical_sources.setdefault(target, []).append(root)

snapshot = {
    "knownRoots": known,
    "canonicalSymlinkTargets": [
        {"target": target, "sourcePaths": sorted(sources), "fingerprint": fingerprint(target)}
        for target, sources in sorted(canonical_sources.items())
    ],
}
with open(destination, "w", encoding="utf-8") as stream:
    json.dump(snapshot, stream, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
PY
}

compare_fingerprints() {
    python3 - "$1" "$2" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as stream: before = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream: after = json.load(stream)
if before == after: raise SystemExit(0)
print("Acceptance isolation failure: real-user path fingerprint changed.", file=sys.stderr)
before_items = {item["path"]: item for item in before["knownRoots"]}
after_items = {item["path"]: item for item in after["knownRoots"]}
changed = []
for path in sorted(set(before_items) | set(after_items)):
    if before_items.get(path) != after_items.get(path): changed.append(path)
before_targets = {item["target"]: item for item in before["canonicalSymlinkTargets"]}
after_targets = {item["target"]: item for item in after["canonicalSymlinkTargets"]}
changed.extend(
    f"{path} (canonical symlink target)"
    for path in sorted(set(before_targets) | set(after_targets))
    if before_targets.get(path) != after_targets.get(path)
)
for path in changed: print(f"  {path}", file=sys.stderr)
raise SystemExit(1)
PY
}

release_acceptance_lock() {
    if [[ "$acceptance_lock_acquired" -eq 1 ]]; then
        local owner_file="$acceptance_lock_dir/owner-token"
        local expected_owner="${runner_pid}:${acceptance_lock_token}"
        local actual_owner=""
        if [[ -f "$owner_file" ]]; then
            actual_owner="$(cat "$owner_file" 2>/dev/null || true)"
        fi
        if [[ "$actual_owner" == "$expected_owner" ]]; then
            rm -f "$owner_file"
            rmdir "$acceptance_lock_dir" 2>/dev/null || true
        fi
        acceptance_lock_acquired=0
    fi
}

acquire_acceptance_lock() {
    local waited=0
    while ! mkdir "$acceptance_lock_dir" 2>/dev/null; do
        if mkdir "$acceptance_lock_reclaim_dir" 2>/dev/null; then
            local owner_file="$acceptance_lock_dir/owner-token"
            local owner=""
            local owner_pid=""
            local stale=0
            if [[ -f "$owner_file" ]]; then
                owner="$(cat "$owner_file" 2>/dev/null || true)"
                owner_pid="${owner%%:*}"
                if [[ "$owner_pid" =~ ^[1-9][0-9]*$ ]] && ! kill -0 "$owner_pid" 2>/dev/null; then
                    stale=1
                fi
            elif [[ "$waited" -ge 10 ]]; then
                # A runner killed between mkdir and owner-token creation must
                # not leave every later acceptance run blocked forever.
                stale=1
            fi
            if [[ "$stale" -eq 1 ]]; then
                local quarantine="${acceptance_lock_dir}.stale.${runner_pid}.${RANDOM}"
                if mv "$acceptance_lock_dir" "$quarantine" 2>/dev/null; then
                    rm -rf "$quarantine"
                fi
            fi
            rmdir "$acceptance_lock_reclaim_dir" 2>/dev/null || true
        fi
        sleep 0.1
        waited=$((waited + 1))
        if [[ "$waited" -ge 72000 ]]; then
            printf 'Acceptance isolation lock timed out: %s\n' "$acceptance_lock_dir" >&2
            return 1
        fi
    done
    acceptance_lock_token="$(python3 - <<'PY'
import secrets
print(secrets.token_hex(16))
PY
)"
    printf '%s:%s\n' "$runner_pid" "$acceptance_lock_token" >"$acceptance_lock_dir/owner-token"
    acceptance_lock_acquired=1
}

process_group_is_alive() {
    [[ -n "$child_pid" ]] && kill -0 -- "-$child_pid" 2>/dev/null
}

stop_timeout_watcher() {
    if [[ -n "$timeout_watcher_pid" ]]; then
        kill "$timeout_watcher_pid" 2>/dev/null || true
        wait "$timeout_watcher_pid" 2>/dev/null || true
        timeout_watcher_pid=""
    fi
}

terminate_process_group() {
    stop_timeout_watcher
    if [[ -z "$child_pid" ]]; then return; fi
    if [[ -f "$process_group_ready" ]]; then
        kill -TERM -- "-$child_pid" 2>/dev/null || true
        for _ in {1..40}; do
            if ! process_group_is_alive; then break; fi
            sleep 0.05
        done
        if process_group_is_alive; then kill -KILL -- "-$child_pid" 2>/dev/null || true; fi
    elif kill -0 "$child_pid" 2>/dev/null; then
        kill -TERM "$child_pid" 2>/dev/null || true
    fi
    wait "$child_pid" 2>/dev/null || true
}

cleanup() {
    local status=$?
    trap - EXIT INT TERM HUP
    set +e
    terminate_process_group
    local fingerprint_after="$isolation_root/real-paths.after.json"
    if ! write_fingerprint "$fingerprint_after" "$real_home" "${real_known_paths[@]}"; then
        printf 'Acceptance isolation failure: unable to fingerprint real-user paths after the run.\n' >&2
        status=1
    elif ! compare_fingerprints "$fingerprint_before" "$fingerprint_after"; then
        status=1
    fi
    if [[ "${AGAS_KEEP_ACCEPTANCE_SNAPSHOT:-0}" == 1 ]]; then
        printf 'Acceptance isolation root kept at %s\n' "$isolation_root"
    else
        rm -rf "$isolation_root"
    fi
    release_acceptance_lock
    exit "$status"
}

forward_signal() {
    local status="$1"
    if [[ -f "$timeout_marker" ]]; then status=124; fi
    trap - INT TERM HUP
    terminate_process_group
    exit "$status"
}

trap cleanup EXIT
trap 'forward_signal 130' INT
trap 'forward_signal 143' TERM
trap 'forward_signal 129' HUP

acquire_acceptance_lock
write_fingerprint "$fingerprint_before" "$real_home" "${real_known_paths[@]}"

export HOME="$isolation_root/home"
export AGAS_HOME="$isolation_root/agas-home"
export XDG_CONFIG_HOME="$isolation_root/xdg/config"
export XDG_DATA_HOME="$isolation_root/xdg/data"
export XDG_CACHE_HOME="$isolation_root/xdg/cache"
export GODOT_USER_HOME="$isolation_root/godot-user-home"
export AGAS_TEST_MANAGED_PROJECT_ROOT="$isolation_root/managed-projects"
export DOTNET_CLI_HOME="$isolation_root/dotnet-cli-home"
export DOTNET_ROOT="$dotnet_root"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
if [[ -n "$nuget_packages" ]]; then export NUGET_PACKAGES="$nuget_packages";
else export NUGET_PACKAGES="$isolation_root/nuget/packages"; fi

mkdir -p \
    "$HOME" \
    "$AGAS_HOME/import-inbox/assets" \
    "$AGAS_HOME/import-inbox/scene-packages" \
    "$XDG_CONFIG_HOME" \
    "$XDG_DATA_HOME" \
    "$XDG_CACHE_HOME" \
    "$GODOT_USER_HOME/app_userdata/$app_name" \
    "$AGAS_TEST_MANAGED_PROJECT_ROOT" \
    "$DOTNET_CLI_HOME" \
    "$snapshot"
if [[ -z "$nuget_packages" ]]; then mkdir -p "$NUGET_PACKAGES"; fi

run_isolated_command() {
    rm -f "$process_group_ready" "$timeout_marker"
    python3 - "$process_group_ready" "$@" <<'PY' &
import os
import sys
ready_path, command, *arguments = sys.argv[1:]
os.setsid()
with open(ready_path, "x", encoding="utf-8") as stream: stream.write(str(os.getpid()))
os.execv(command, [command, *arguments])
PY
    child_pid=$!
    (
        sleep "$command_timeout_seconds"
        : >"$timeout_marker"
        kill -TERM "$runner_pid" 2>/dev/null || true
    ) &
    timeout_watcher_pid=$!
    set +e
    wait "$child_pid"
    local status=$?
    set -e
    stop_timeout_watcher
    terminate_process_group
    child_pid=""
    return "$status"
}

printf 'Creating disposable acceptance snapshot: %s\n' "$snapshot"
rsync -a \
    --exclude='.git/' \
    --exclude='.godot/' \
    --exclude='bin/' \
    --exclude='obj/' \
    "$project_root/" "$snapshot/"

python3 - "$snapshot/project.godot" <<'PY'
from pathlib import Path
import sys
project_file = Path(sys.argv[1])
source = project_file.read_text(encoding="utf-8")
production = 'project/assembly_name="APP-AGAS-CS"'
acceptance = 'project/assembly_name="APP-AGAS-CS.Godot.Acceptance"'
if source.count(production) != 1:
    raise SystemExit("project.godot must contain the production assembly name exactly once")
project_file.write_text(source.replace(production, acceptance), encoding="utf-8")
PY

run_isolated_command \
    "$dotnet_physical_path" build "$snapshot/tests/Godot/APP-AGAS-CS.Godot.Acceptance.csproj" -c Debug

acceptance_output="$snapshot/tests/Godot/bin/Debug"
godot_output="$snapshot/.godot/mono/temp/bin/Debug"
mkdir -p "$godot_output"
cp -R "$acceptance_output/." "$godot_output/"

if [[ -e "$godot_output/APP-AGAS-CS.dll" ]]; then
    printf 'Isolation failure: acceptance staging created a production-named DLL.\n' >&2
    exit 1
fi

run_isolated_command \
    "$godot_bin" --headless --path "$snapshot" "res://tests/Godot/$scene"
