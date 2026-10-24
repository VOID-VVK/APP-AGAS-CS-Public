#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
runner="$script_dir/run-core-isolated.sh"
self_test_root="$(mktemp -d "${TMPDIR:-/tmp}/agas-core-runner-self-test.XXXXXX")"

cleanup() {
    rm -rf "$self_test_root"
}
trap cleanup EXIT

fake_bin="$self_test_root/bin"
mkdir -p "$fake_bin/sdk"
cat >"$fake_bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail

printf '%s\0' "$@" >"$AGAS_RUNNER_TEST_LOG.args"
env | sort >"$AGAS_RUNNER_TEST_LOG.env"
printf '%s' "$HOME" >"$AGAS_RUNNER_TEST_LOG.isolated-home"
printf '%s' "$$" >"$AGAS_RUNNER_TEST_LOG.pid"

for name in \
    HOME \
    AGAS_HOME \
    XDG_DATA_HOME \
    XDG_CONFIG_HOME \
    XDG_CACHE_HOME \
    GODOT_USER_HOME \
    AGAS_TEST_MANAGED_PROJECT_ROOT
do
    if [[ ! -d "${!name}" ]]; then
        printf 'missing isolated directory %s=%s\n' "$name" "${!name}" >&2
        exit 98
    fi
done
[[ -x "$DOTNET_ROOT/dotnet" && -d "$DOTNET_ROOT/sdk" ]] || exit 98
[[ -d "$NUGET_PACKAGES" ]] || exit 98
[[ -d "$AGAS_HOME/import-inbox/assets" ]] || exit 98
[[ -d "$AGAS_HOME/import-inbox/scene-packages" ]] || exit 98
printf 'validated\n' >"$AGAS_RUNNER_TEST_LOG.paths-validated"

case "${AGAS_RUNNER_TEST_MODE:-success}" in
    success)
        exit 0
        ;;
    failure)
        exit 23
        ;;
    ignore-term-tree)
        bash -c 'trap "" TERM; printf "%s" "$$" >"$1"; while :; do sleep 1; done' \
            _ "$AGAS_RUNNER_TEST_LOG.grandchild-pid" &
        printf '%s' "$!" >"$AGAS_RUNNER_TEST_LOG.spawned-grandchild-pid"
        trap '' TERM
        while :; do
            sleep 1
        done
        ;;
    mutate-real)
        printf 'changed\n' >>"$AGAS_RUNNER_TEST_REAL_MUTATION_PATH"
        exit 0
        ;;
    *)
        exit 99
        ;;
esac
FAKE
chmod +x "$fake_bin/dotnet"

setup_case() {
    local name="$1"
    case_root="$self_test_root/$name"
    real_home="$case_root/real-home"
    real_xdg_config="$case_root/real-xdg/config"
    real_xdg_data="$case_root/real-xdg/data"
    real_xdg_cache="$case_root/real-xdg/cache"
    case_tmp="$case_root/tmp"
    log="$case_root/invocation"

    mkdir -p \
        "$real_home/.agas" \
        "$real_home/Documents/AGAS Projects" \
        "$real_xdg_config/APP-AGAS-CS" \
        "$real_xdg_data/APP-AGAS-CS" \
        "$real_xdg_cache/APP-AGAS-CS" \
        "$real_home/.nuget/packages" \
        "$case_tmp"
    printf 'unchanged\n' >"$real_home/.agas/sentinel"
    printf 'unchanged\n' >"$real_home/Documents/AGAS Projects/sentinel"
    printf 'unchanged\n' >"$real_xdg_config/APP-AGAS-CS/sentinel"
    printf 'unchanged\n' >"$real_xdg_data/APP-AGAS-CS/sentinel"
    printf 'unchanged\n' >"$real_xdg_cache/APP-AGAS-CS/sentinel"

    if [[ "$(uname -s)" == Darwin ]]; then
        godot_real="$real_home/Library/Application Support/Godot/app_userdata/APP-AGAS-CS"
    else
        godot_real="$real_xdg_data/godot/app_userdata/APP-AGAS-CS"
    fi
    mkdir -p "$godot_real"
    printf 'unchanged\n' >"$godot_real/sentinel"
}

run_runner() {
    env \
        PATH="$fake_bin:$PATH" \
        HOME="$real_home" \
        XDG_CONFIG_HOME="$real_xdg_config" \
        XDG_DATA_HOME="$real_xdg_data" \
        XDG_CACHE_HOME="$real_xdg_cache" \
        TMPDIR="$case_tmp" \
        AGAS_RUNNER_TEST_LOG="$log" \
        AGAS_RUNNER_TEST_MODE="$1" \
        AGAS_RUNNER_TEST_REAL_MUTATION_PATH="${2:-$real_home/.agas/sentinel}" \
        "$runner" "${runner_args[@]}"
}

assert_cleaned() {
    local isolated_home
    isolated_home="$(cat "$log.isolated-home")"
    local isolated_root
    isolated_root="$(dirname "$isolated_home")"
    if [[ -e "$isolated_root" ]]; then
        printf 'Self-test failure: isolation root was not cleaned: %s\n' "$isolated_root" >&2
        exit 1
    fi
}

setup_case normal
runner_args=(--no-build --filter 'FullyQualifiedName~Example.Test' --logger 'console;verbosity=minimal')
run_runner success

python3 - "$log.args" <<'PY'
import sys

with open(sys.argv[1], "rb") as stream:
    actual = stream.read().split(b"\0")
if actual[-1] == b"":
    actual.pop()
expected = [
    b"test",
    b"tests/Core/APP-AGAS-CS.Core.Tests.csproj",
    b"-c",
    b"Debug",
    b"--no-build",
    b"--filter",
    b"FullyQualifiedName~Example.Test",
    b"--logger",
    b"console;verbosity=minimal",
]
if actual != expected:
    raise SystemExit(f"argument forwarding mismatch: {actual!r}")
PY

python3 - "$log.env" "$real_home" <<'PY'
import os
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    environment = dict(line.rstrip("\n").split("=", 1) for line in stream if "=" in line)

required = [
    "HOME",
    "AGAS_HOME",
    "XDG_DATA_HOME",
    "XDG_CONFIG_HOME",
    "XDG_CACHE_HOME",
    "GODOT_USER_HOME",
    "AGAS_TEST_MANAGED_PROJECT_ROOT",
]
values = [environment[name] for name in required]
if environment["HOME"] == sys.argv[2]:
    raise SystemExit("HOME was not isolated")
if len(set(values)) != len(values):
    raise SystemExit(f"isolation paths are not independent: {values!r}")
for name in required:
    value = environment[name]
    if os.path.realpath(value) != value:
        raise SystemExit(f"{name} is not canonical: {value!r} -> {os.path.realpath(value)!r}")
if environment["NUGET_PACKAGES"] != os.path.realpath(os.path.join(sys.argv[2], ".nuget", "packages")):
    raise SystemExit("the existing package cache was not retained")
PY
if [[ ! -f "$log.paths-validated" ]]; then
    printf 'Self-test failure: fake dotnet did not observe all isolated paths/inboxes.\n' >&2
    exit 1
fi
assert_cleaned

setup_case failure
runner_args=(--no-build)
set +e
run_runner failure
failure_status=$?
set -e
if [[ "$failure_status" -ne 23 ]]; then
    printf 'Self-test failure: child exit 23 became %s.\n' "$failure_status" >&2
    exit 1
fi
assert_cleaned

setup_case signal
runner_args=(--no-build)
(
    exec env \
        PATH="$fake_bin:$PATH" \
        HOME="$real_home" \
        XDG_CONFIG_HOME="$real_xdg_config" \
        XDG_DATA_HOME="$real_xdg_data" \
        XDG_CACHE_HOME="$real_xdg_cache" \
        TMPDIR="$case_tmp" \
        AGAS_RUNNER_TEST_LOG="$log" \
        AGAS_RUNNER_TEST_MODE=ignore-term-tree \
        AGAS_RUNNER_TEST_REAL_MUTATION_PATH="$real_home/.agas/sentinel" \
        "$runner" "${runner_args[@]}"
) &
runner_pid=$!
for _ in {1..100}; do
    [[ -f "$log.grandchild-pid" ]] && break
    sleep 0.05
done
if [[ ! -f "$log.grandchild-pid" ]]; then
    printf 'Self-test failure: timeout fixture did not start.\n' >&2
    kill -TERM "$runner_pid" 2>/dev/null || true
    exit 1
fi
fake_pid="$(cat "$log.pid")"
grandchild_pid="$(cat "$log.grandchild-pid")"
kill -TERM "$runner_pid"
set +e
wait "$runner_pid"
signal_status=$?
set -e
if [[ "$signal_status" -ne 143 ]]; then
    printf 'Self-test failure: TERM exit status was %s instead of 143.\n' "$signal_status" >&2
    exit 1
fi
for _ in {1..100}; do
    if ! kill -0 "$fake_pid" 2>/dev/null && ! kill -0 "$grandchild_pid" 2>/dev/null; then
        break
    fi
    sleep 0.05
done
if kill -0 "$fake_pid" 2>/dev/null || kill -0 "$grandchild_pid" 2>/dev/null; then
    printf 'Self-test failure: TERM cleanup left fake dotnet or its TERM-ignoring grandchild alive.\n' >&2
    exit 1
fi
assert_cleaned

setup_case broad-symlink
rm -rf "$real_home/.agas"
ln -s "$real_home" "$real_home/.agas"
runner_args=(--no-build)
set +e
run_runner success
broad_symlink_status=$?
set -e
if [[ "$broad_symlink_status" -eq 0 ]]; then
    printf 'Self-test failure: a HOME-wide known-root symlink was not rejected.\n' >&2
    exit 1
fi
if [[ -f "$log.pid" ]]; then
    printf 'Self-test failure: fake dotnet started despite a broad known-root symlink.\n' >&2
    exit 1
fi
if find "$case_tmp" -maxdepth 1 -name 'agas-core-isolated.*' -print -quit | grep -q .; then
    printf 'Self-test failure: broad-symlink rejection left an isolation root behind.\n' >&2
    exit 1
fi

setup_case casing-alias-broad-symlink
casing_alias="$case_root/REAL-HOME"
if [[ "$casing_alias" != "$real_home" && -d "$casing_alias" ]]; then
    rm -rf "$real_home/.agas"
    ln -s "$casing_alias" "$real_home/.agas"
    runner_args=(--no-build)
    set +e
    run_runner success
    casing_alias_status=$?
    set -e
    if [[ "$casing_alias_status" -eq 0 ]]; then
        printf 'Self-test failure: a casing-alias HOME symlink was not rejected.\n' >&2
        exit 1
    fi
    if [[ -f "$log.pid" ]]; then
        printf 'Self-test failure: fake dotnet started despite a casing-alias HOME symlink.\n' >&2
        exit 1
    fi
    if find "$case_tmp" -maxdepth 1 -name 'agas-core-isolated.*' -print -quit | grep -q .; then
        printf 'Self-test failure: casing-alias rejection left an isolation root behind.\n' >&2
        exit 1
    fi
else
    printf 'casing-alias broad-symlink self-test: SKIP (case-sensitive filesystem)\n'
fi

setup_case fingerprint
runner_args=(--no-build)
set +e
run_runner mutate-real "$real_home/.agas/sentinel"
fingerprint_status=$?
set -e
if [[ "$fingerprint_status" -ne 1 ]]; then
    printf 'Self-test failure: a real-path mutation was not rejected (status %s).\n' "$fingerprint_status" >&2
    exit 1
fi
assert_cleaned

setup_case symlink-fingerprint
symlink_target="$case_root/agas-symlink-target"
rm -rf "$real_home/.agas"
mkdir -p "$symlink_target"
printf 'unchanged\n' >"$symlink_target/sentinel"
ln -s "$symlink_target" "$real_home/.agas"
runner_args=(--no-build)
set +e
run_runner mutate-real "$symlink_target/sentinel"
symlink_fingerprint_status=$?
set -e
if [[ "$symlink_fingerprint_status" -ne 1 ]]; then
    printf 'Self-test failure: a known-root symlink target mutation was not rejected (status %s).\n' \
        "$symlink_fingerprint_status" >&2
    exit 1
fi
assert_cleaned

printf 'run-core-isolated self-test: PASS\n'
