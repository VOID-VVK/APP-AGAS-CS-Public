#!/usr/bin/env bash

set -euo pipefail

umask 077

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
project_root="$(cd "$script_dir/.." && pwd -P)"
core_project="tests/Core/APP-AGAS-CS.Core.Tests.csproj"
app_name="APP-AGAS-CS"

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

# Resolve every real-user path before replacing the process environment.
real_home="${HOME:-}"
if [[ -z "$real_home" ]]; then
    printf 'HOME must be set so the real-user isolation boundary can be fingerprinted.\n' >&2
    exit 2
fi
real_home="$(cd "$real_home" 2>/dev/null && pwd -P)" || {
    printf 'HOME must identify an existing directory: %s\n' "${HOME:-}" >&2
    exit 2
}
real_xdg_config_home="${XDG_CONFIG_HOME:-$real_home/.config}"
real_xdg_data_home="${XDG_DATA_HOME:-$real_home/.local/share}"
real_xdg_cache_home="${XDG_CACHE_HOME:-$real_home/.cache}"
real_nuget_packages="${NUGET_PACKAGES:-$real_home/.nuget/packages}"

# HOME isolation must not hide the installed SDK. Prefer an already valid
# DOTNET_ROOT, then recognize both a conventional installation and Homebrew's
# bin-wrapper/libexec layout without launching dotnet before isolation is set.
dotnet_root="${DOTNET_ROOT:-}"
if [[ -z "$dotnet_root" || ! -x "$dotnet_root/dotnet" || ! -d "$dotnet_root/sdk" ]]; then
    dotnet_command_directory="$(dirname "$dotnet_physical_path")"
    if [[ -x "$dotnet_command_directory/dotnet" && -d "$dotnet_command_directory/sdk" ]]; then
        dotnet_root="$dotnet_command_directory"
    elif [[ -x "$dotnet_command_directory/../libexec/dotnet" && -d "$dotnet_command_directory/../libexec/sdk" ]]; then
        dotnet_root="$(cd "$dotnet_command_directory/../libexec" && pwd -P)"
    else
        printf 'Unable to locate DOTNET_ROOT for %s without starting dotnet before isolation.\n' "$dotnet_bin" >&2
        exit 2
    fi
fi

# Reuse only the package cache location. NuGet configuration/credentials remain
# hidden by the isolated HOME/XDG roots. A missing cache gets a disposable root.
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
    "$real_home/Documents/AGAS Projects"
    "$real_godot_app_data"
    "$real_xdg_config_home/$app_name"
    "$real_xdg_data_home/$app_name"
    "$real_xdg_cache_home/$app_name"
)

isolation_root="$(mktemp -d "${TMPDIR:-/tmp}/agas-core-isolated.XXXXXX")"
# macOS commonly exposes /var as an alias of /private/var. Export one physical,
# canonical case root so every isolated child path has a single spelling and
# path-identity assertions do not accidentally compare aliases.
isolation_root="$(cd "$isolation_root" && pwd -P)"
fingerprint_before="$isolation_root/real-paths.before.json"
child_pid=""
process_group_ready="$isolation_root/dotnet-process-group.ready"

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

def kind(mode):
    if stat.S_ISREG(mode):
        return "file"
    if stat.S_ISDIR(mode):
        return "directory"
    if stat.S_ISLNK(mode):
        return "symlink"
    return "other"

def describe(path, relative):
    info = os.lstat(path)
    item = {
        "relativePath": relative,
        "type": kind(info.st_mode),
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

known_roots = [fingerprint(root) for root in roots]
canonical_sources = {}

def path_ancestors(path):
    current = os.path.abspath(path)
    while True:
        yield current
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent

def paths_identify_same_object(left, right):
    if os.path.normpath(left) == os.path.normpath(right):
        return True
    try:
        return os.path.samefile(left, right)
    except (FileNotFoundError, NotADirectoryError, OSError):
        return False

def is_string_ancestor_or_equal(candidate, descendant):
    try:
        return os.path.commonpath([candidate, descendant]) == candidate
    except ValueError:
        return False

for root in roots:
    if not os.path.islink(root):
        continue
    canonical_target = os.path.realpath(root)
    if os.path.islink(canonical_target):
        raise SystemExit(f"known root has an unresolvable symlink cycle: {root}")

    # Following a known-root link to HOME, a HOME ancestor, or a parent of
    # another known root would turn this bounded check into a broad filesystem
    # scan. Identity checks are required because APFS can resolve differently
    # cased spellings to the same inode while realpath preserves their casing.
    # Reject it before dotnet starts; exact external target directories remain
    # supported.
    broad_target = is_string_ancestor_or_equal(canonical_target, protected_home)
    if not broad_target:
        broad_target = any(
            paths_identify_same_object(canonical_target, ancestor)
            for ancestor in path_ancestors(protected_home)
        )
    if not broad_target:
        for other_root in roots:
            if other_root == root:
                continue
            other_target = os.path.realpath(other_root)
            if paths_identify_same_object(canonical_target, other_target):
                continue
            other_ancestors = list(path_ancestors(other_target))[1:]
            if is_string_ancestor_or_equal(canonical_target, other_target) or any(
                paths_identify_same_object(canonical_target, ancestor)
                for ancestor in other_ancestors
            ):
                broad_target = True
                break
    if broad_target:
        raise SystemExit(
            f"known root symlink target is too broad to fingerprint safely: {root} -> {canonical_target}"
        )
    canonical_sources.setdefault(canonical_target, []).append(root)

canonical_targets = [
    {
        "target": target,
        "sourcePaths": sorted(sources),
        "fingerprint": fingerprint(target),
    }
    for target, sources in sorted(canonical_sources.items())
]
snapshot = {
    "knownRoots": known_roots,
    "canonicalSymlinkTargets": canonical_targets,
}
with open(destination, "w", encoding="utf-8") as stream:
    json.dump(snapshot, stream, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
PY
}

compare_fingerprints() {
    python3 - "$1" "$2" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    before = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream:
    after = json.load(stream)

if before == after:
    raise SystemExit(0)

before_by_path = {item["path"]: item for item in before["knownRoots"]}
after_by_path = {item["path"]: item for item in after["knownRoots"]}
changed = sorted(
    path for path in set(before_by_path) | set(after_by_path)
    if before_by_path.get(path) != after_by_path.get(path)
)
before_targets = {item["target"]: item for item in before["canonicalSymlinkTargets"]}
after_targets = {item["target"]: item for item in after["canonicalSymlinkTargets"]}
changed.extend(
    f"{path} (canonical symlink target)"
    for path in sorted(set(before_targets) | set(after_targets))
    if before_targets.get(path) != after_targets.get(path)
)
print("Core isolation failure: real-user path fingerprint changed:", file=sys.stderr)
for path in changed:
    print(f"  {path}", file=sys.stderr)
raise SystemExit(1)
PY
}

process_group_is_alive() {
    [[ -n "$child_pid" ]] && kill -0 -- "-$child_pid" 2>/dev/null
}

terminate_process_group() {
    if [[ -z "$child_pid" ]]; then
        return
    fi

    if [[ -f "$process_group_ready" ]]; then
        kill -TERM -- "-$child_pid" 2>/dev/null || true
        for _ in {1..40}; do
            if ! process_group_is_alive; then
                break
            fi
            sleep 0.05
        done
        if process_group_is_alive; then
            kill -KILL -- "-$child_pid" 2>/dev/null || true
        fi
    elif kill -0 "$child_pid" 2>/dev/null; then
        # The launcher has not completed setsid(), so a negative PGID could
        # target the runner's own group. Only the direct launcher is safe here.
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
        printf 'Core isolation failure: unable to fingerprint real-user paths after the test.\n' >&2
        status=1
    elif ! compare_fingerprints "$fingerprint_before" "$fingerprint_after"; then
        status=1
    fi

    rm -rf "$isolation_root"
    exit "$status"
}

forward_signal() {
    local status="$1"
    trap - INT TERM HUP
    terminate_process_group
    exit "$status"
}

trap cleanup EXIT
trap 'forward_signal 130' INT
trap 'forward_signal 143' TERM
trap 'forward_signal 129' HUP

write_fingerprint "$fingerprint_before" "$real_home" "${real_known_paths[@]}"

export HOME="$isolation_root/home"
export AGAS_HOME="$isolation_root/agas-home"
export XDG_DATA_HOME="$isolation_root/xdg/data"
export XDG_CONFIG_HOME="$isolation_root/xdg/config"
export XDG_CACHE_HOME="$isolation_root/xdg/cache"
export GODOT_USER_HOME="$isolation_root/godot-user-home"
export AGAS_TEST_MANAGED_PROJECT_ROOT="$isolation_root/managed-projects"
export DOTNET_CLI_HOME="$isolation_root/dotnet-cli-home"
export DOTNET_ROOT="$dotnet_root"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
if [[ -n "$nuget_packages" ]]; then
    export NUGET_PACKAGES="$nuget_packages"
else
    export NUGET_PACKAGES="$isolation_root/nuget/packages"
fi

mkdir -p \
    "$HOME" \
    "$AGAS_HOME/import-inbox/assets" \
    "$AGAS_HOME/import-inbox/scene-packages" \
    "$XDG_DATA_HOME" \
    "$XDG_CONFIG_HOME" \
    "$XDG_CACHE_HOME" \
    "$GODOT_USER_HOME/app_userdata/$app_name" \
    "$AGAS_TEST_MANAGED_PROJECT_ROOT" \
    "$DOTNET_CLI_HOME"
if [[ -z "$nuget_packages" ]]; then
    mkdir -p "$NUGET_PACKAGES"
fi

printf 'Core isolation root: %s\n' "$isolation_root"

cd "$project_root"
python3 - "$process_group_ready" "$dotnet_bin" test "$core_project" -c Debug "$@" <<'PY' &
import os
import sys

ready_path, command, *arguments = sys.argv[1:]
os.setsid()
with open(ready_path, "x", encoding="utf-8") as stream:
    stream.write(str(os.getpid()))
os.execv(command, [command, *arguments])
PY
child_pid=$!

set +e
wait "$child_pid"
status=$?
set -e
terminate_process_group
child_pid=""
exit "$status"
