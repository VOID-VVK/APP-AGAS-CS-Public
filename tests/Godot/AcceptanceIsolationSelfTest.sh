#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
runner="$script_dir/run-acceptance.sh"
self_test_root="$(mktemp -d "${TMPDIR:-/tmp}/agas-acceptance-isolation-self-test.XXXXXX")"

cleanup() {
    rm -rf "$self_test_root"
}

fake_bin="$self_test_root/bin"
mkdir -p "$fake_bin/sdk"

cat >"$fake_bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
for name in HOME AGAS_HOME XDG_CONFIG_HOME XDG_DATA_HOME XDG_CACHE_HOME GODOT_USER_HOME AGAS_TEST_MANAGED_PROJECT_ROOT DOTNET_CLI_HOME; do
    [[ -d "${!name}" ]] || { printf 'missing isolated %s=%s\n' "$name" "${!name}" >&2; exit 98; }
done
[[ -d "$AGAS_HOME/import-inbox/assets" ]] || exit 98
[[ -d "$AGAS_HOME/import-inbox/scene-packages" ]] || exit 98
[[ "$1" == build && "$2" == */tests/Godot/APP-AGAS-CS.Godot.Acceptance.csproj ]] || exit 97
output="$(dirname "$2")/bin/Debug"
mkdir -p "$output"
printf 'fixture' >"$output/APP-AGAS-CS.Godot.Acceptance.dll"
printf '%s' "$HOME" >"$AGAS_ACCEPTANCE_SELF_TEST_LOG.dotnet-home"
FAKE
chmod +x "$fake_bin/dotnet"

cat >"$fake_bin/godot-mono" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
printf '%s' "$HOME" >"$AGAS_ACCEPTANCE_SELF_TEST_LOG.godot-home"
printf '%s' "$$" >"$AGAS_ACCEPTANCE_SELF_TEST_LOG.godot-pid"
for name in HOME AGAS_HOME XDG_CONFIG_HOME XDG_DATA_HOME XDG_CACHE_HOME GODOT_USER_HOME AGAS_TEST_MANAGED_PROJECT_ROOT; do
    [[ -d "${!name}" ]] || exit 98
done
[[ "$HOME" != "$AGAS_ACCEPTANCE_SELF_TEST_REAL_HOME" ]] || exit 98
[[ "$AGAS_HOME" != "$AGAS_ACCEPTANCE_SELF_TEST_REAL_AGAS" ]] || exit 98
case "${AGAS_ACCEPTANCE_SELF_TEST_MODE:-success}" in
    success)
        exit 0
        ;;
    success-tree)
        bash -c 'trap "" TERM; printf "%s" "$$" >"$1"; while :; do sleep 1; done' \
            _ "$AGAS_ACCEPTANCE_SELF_TEST_LOG.grandchild-pid" &
        for _ in {1..100}; do [[ -f "$AGAS_ACCEPTANCE_SELF_TEST_LOG.grandchild-pid" ]] && break; sleep 0.01; done
        exit 0
        ;;
    failure)
        exit 23
        ;;
    failure-tree)
        bash -c 'trap "" TERM; printf "%s" "$$" >"$1"; while :; do sleep 1; done' \
            _ "$AGAS_ACCEPTANCE_SELF_TEST_LOG.grandchild-pid" &
        for _ in {1..100}; do [[ -f "$AGAS_ACCEPTANCE_SELF_TEST_LOG.grandchild-pid" ]] && break; sleep 0.01; done
        exit 23
        ;;
    mutate-real)
        printf 'changed\n' >>"$AGAS_ACCEPTANCE_SELF_TEST_MUTATION_PATH"
        exit 0
        ;;
    hold)
        : >"$AGAS_ACCEPTANCE_SELF_TEST_LOCK_MARKER.started"
        sleep 1
        : >"$AGAS_ACCEPTANCE_SELF_TEST_LOCK_MARKER.finished"
        exit 0
        ;;
    hang-tree)
        bash -c 'trap "" TERM; printf "%s" "$$" >"$1"; while :; do sleep 1; done' \
            _ "$AGAS_ACCEPTANCE_SELF_TEST_LOG.grandchild-pid" &
        trap '' TERM
        while :; do sleep 1; done
        ;;
    *)
        exit 99
        ;;
esac
FAKE
chmod +x "$fake_bin/godot-mono"

setup_case() {
    local name="$1"
    case_root="$self_test_root/$name"
    real_home="$case_root/real-home"
    real_agas="$case_root/real-agas"
    real_xdg_config="$case_root/real-xdg/config"
    real_xdg_data="$case_root/real-xdg/data"
    real_xdg_cache="$case_root/real-xdg/cache"
    case_tmp="$case_root/tmp"
    log="$case_root/log"
    mkdir -p \
        "$real_home/.agas" \
        "$real_agas" \
        "$real_home/Documents/AGAS Projects" \
        "$real_xdg_config/APP-AGAS-CS" \
        "$real_xdg_data/APP-AGAS-CS" \
        "$real_xdg_cache/APP-AGAS-CS" \
        "$real_home/.nuget/packages" \
        "$case_tmp"
    if [[ "$(uname -s)" == Darwin ]]; then
        godot_real="$real_home/Library/Application Support/Godot/app_userdata/APP-AGAS-CS"
    else
        godot_real="$real_xdg_data/godot/app_userdata/APP-AGAS-CS"
    fi
    mkdir -p "$godot_real"
    for path in \
        "$real_home/.agas/sentinel" \
        "$real_agas/sentinel" \
        "$real_home/Documents/AGAS Projects/sentinel" \
        "$real_xdg_config/APP-AGAS-CS/sentinel" \
        "$real_xdg_data/APP-AGAS-CS/sentinel" \
        "$real_xdg_cache/APP-AGAS-CS/sentinel" \
        "$godot_real/sentinel"
    do
        printf 'unchanged\n' >"$path"
    done
}

run_case() {
    local mode="$1"
    env \
        PATH="$fake_bin:$PATH" \
        HOME="$real_home" \
        AGAS_HOME="$real_agas" \
        XDG_CONFIG_HOME="$real_xdg_config" \
        XDG_DATA_HOME="$real_xdg_data" \
        XDG_CACHE_HOME="$real_xdg_cache" \
        TMPDIR="$case_tmp" \
        GODOT_MONO_BIN="$fake_bin/godot-mono" \
        AGAS_ACCEPTANCE_COMMAND_TIMEOUT_SECONDS="${AGAS_ACCEPTANCE_SELF_TEST_TIMEOUT:-10}" \
        AGAS_ACCEPTANCE_SELF_TEST_LOG="$log" \
        AGAS_ACCEPTANCE_SELF_TEST_MODE="$mode" \
        AGAS_ACCEPTANCE_SELF_TEST_REAL_HOME="$real_home" \
        AGAS_ACCEPTANCE_SELF_TEST_REAL_AGAS="$real_agas" \
        AGAS_ACCEPTANCE_SELF_TEST_MUTATION_PATH="$real_agas/sentinel" \
        AGAS_ACCEPTANCE_SELF_TEST_LOCK_MARKER="$log" \
        "$runner" ProjectPickerAcceptance.tscn
}

assert_isolation_cleaned() {
    local isolated_home
    isolated_home="$(cat "$log.godot-home")"
    local isolated_root
    isolated_root="$(dirname "$isolated_home")"
    [[ ! -e "$isolated_root" ]] || {
        printf 'Self-test failure: isolation root remains: %s\n' "$isolated_root" >&2
        exit 1
    }
}

assert_logged_tree_dead() {
    local tree_log="$1"
    local process_id
    process_id="$(cat "$tree_log.grandchild-pid")"
    for _ in {1..100}; do
        if ! kill -0 "$process_id" 2>/dev/null; then return; fi
        sleep 0.05
    done
    printf 'Self-test failure: child process tree remains alive: %s\n' "$process_id" >&2
    exit 1
}

setup_case normal
run_case success-tree
[[ "$(cat "$log.dotnet-home")" == "$(cat "$log.godot-home")" ]] || {
    printf 'Self-test failure: build and Godot did not share the case isolation root.\n' >&2
    exit 1
}
assert_logged_tree_dead "$log"
assert_isolation_cleaned

setup_case abnormal
set +e
run_case failure-tree
abnormal_status=$?
set -e
[[ "$abnormal_status" -eq 23 ]] || {
    printf 'Self-test failure: abnormal child exit 23 became %s.\n' "$abnormal_status" >&2
    exit 1
}
assert_logged_tree_dead "$log"
assert_isolation_cleaned

setup_case timeout
AGAS_ACCEPTANCE_SELF_TEST_TIMEOUT=1
export AGAS_ACCEPTANCE_SELF_TEST_TIMEOUT
set +e
run_case hang-tree
timeout_status=$?
set -e
unset AGAS_ACCEPTANCE_SELF_TEST_TIMEOUT
[[ "$timeout_status" -eq 124 ]] || {
    printf 'Self-test failure: timeout exit was %s instead of 124.\n' "$timeout_status" >&2
    exit 1
}
godot_pid="$(cat "$log.godot-pid")"
grandchild_pid="$(cat "$log.grandchild-pid")"
for _ in {1..100}; do
    if ! kill -0 "$godot_pid" 2>/dev/null && ! kill -0 "$grandchild_pid" 2>/dev/null; then break; fi
    sleep 0.05
done
if kill -0 "$godot_pid" 2>/dev/null || kill -0 "$grandchild_pid" 2>/dev/null; then
    printf 'Self-test failure: timeout left Godot process tree alive.\n' >&2
    exit 1
fi
assert_isolation_cleaned

setup_case fingerprint
set +e
run_case mutate-real
fingerprint_status=$?
set -e
[[ "$fingerprint_status" -eq 1 ]] || {
    printf 'Self-test failure: real-path mutation was not detected (status %s).\n' "$fingerprint_status" >&2
    exit 1
}
assert_isolation_cleaned

setup_case symlink-fingerprint
symlink_target="$case_root/real-agas-target"
rm -rf "$real_agas"
mkdir -p "$symlink_target"
printf 'unchanged\n' >"$symlink_target/sentinel"
ln -s "$symlink_target" "$real_agas"
set +e
run_case mutate-real
symlink_fingerprint_status=$?
set -e
[[ "$symlink_fingerprint_status" -eq 1 ]] || {
    printf 'Self-test failure: real-path symlink target mutation was not detected (status %s).\n' "$symlink_fingerprint_status" >&2
    exit 1
}
assert_isolation_cleaned

setup_case parallel
parallel_log_one="$log-one"
parallel_log_two="$log-two"
(
    trap - EXIT
    log="$parallel_log_one"
    run_case success
) &
parallel_one=$!
(
    trap - EXIT
    log="$parallel_log_two"
    run_case success
) &
parallel_two=$!
wait "$parallel_one"
wait "$parallel_two"
home_one="$(cat "$parallel_log_one.godot-home")"
home_two="$(cat "$parallel_log_two.godot-home")"
[[ "$home_one" != "$home_two" ]] || {
    printf 'Self-test failure: parallel cases shared an isolation root.\n' >&2
    exit 1
}
[[ ! -e "$(dirname "$home_one")" && ! -e "$(dirname "$home_two")" ]] || {
    printf 'Self-test failure: parallel isolation roots were not cleaned.\n' >&2
    exit 1
}
[[ -x "$fake_bin/godot-mono" ]] || {
    printf 'Self-test failure: parallel runners removed the shared fake Godot binary.\n' >&2
    exit 1
}

setup_case lock
lock_log_one="$log-one"
lock_log_two="$log-two"
(
    trap - EXIT
    log="$lock_log_one"
    run_case hold
) &
lock_one=$!
for _ in {1..100}; do [[ -f "$lock_log_one.started" ]] && break; sleep 0.05; done
[[ -f "$lock_log_one.started" ]] || {
    printf 'Self-test failure: first lock holder did not start.\n' >&2
    exit 1
}
(
    trap - EXIT
    log="$lock_log_two"
    run_case hold
) &
lock_two=$!
sleep 0.2
[[ ! -f "$lock_log_two.started" ]] || {
    printf 'Self-test failure: acceptance fingerprint lock did not serialize runners.\n' >&2
    exit 1
}
wait "$lock_one"
wait "$lock_two"
[[ -f "$lock_log_two.finished" ]] || {
    printf 'Self-test failure: second lock holder did not complete.\n' >&2
    exit 1
}

setup_case stale-lock
global_lock="/tmp/agas-godot-acceptance-$(id -u).lock"
global_reclaim_lock="${global_lock}.reclaim"
rm -rf "$global_lock" "$global_reclaim_lock" "${global_lock}.stale."*
sleep 30 &
stale_owner_pid=$!
kill "$stale_owner_pid"
wait "$stale_owner_pid" 2>/dev/null || true
mkdir "$global_lock"
printf '%s:stale-token\n' "$stale_owner_pid" >"$global_lock/owner-token"
stale_log_one="$log-one"
stale_log_two="$log-two"
(
    trap - EXIT
    log="$stale_log_one"
    run_case hold
) &
stale_one=$!
(
    trap - EXIT
    log="$stale_log_two"
    run_case hold
) &
stale_two=$!
wait "$stale_one"
wait "$stale_two"
[[ -f "$stale_log_one.finished" && -f "$stale_log_two.finished" ]] || {
    printf 'Self-test failure: stale-lock contenders did not both complete.\n' >&2
    exit 1
}
[[ ! -e "$global_lock" && ! -e "$global_reclaim_lock" ]] || {
    printf 'Self-test failure: stale-lock recovery left a global lock.\n' >&2
    exit 1
}
if compgen -G "${global_lock}.stale.*" >/dev/null; then
    printf 'Self-test failure: stale-lock recovery left a quarantine directory.\n' >&2
    exit 1
fi

printf 'Acceptance isolation self-test: PASS\n'
cleanup
