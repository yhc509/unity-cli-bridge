#!/usr/bin/env bash

set -uo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"

PROJECT_PATH="${UNITY_PROJECT_PATH:-}"
FILTER_PATTERN=""
ALLOW_DESTRUCTIVE=0
DRY_RUN=0
JSON_REPORT=0

TEMP_DIR=""
FIXTURE_ROOT=""
SCENE_TEST_PATH=""
ORIGINAL_ACTIVE_SCENE_PATH=""
OFFLINE_PROJECT_PATH=""
SCREENSHOT_PATH=""
LOCAL_PACKAGE_DIR=""
LOCAL_PACKAGE_NAME="com.yhc509.liveipc.fixture"
LOCAL_PACKAGE_ADDED=0
FIXTURE_ROOT_CREATED=0
PROJECT_GIT_ROOT=""
SCENE_TEST_DEFAULT_PATH="Assets/Scenes/SampleScene.unity"
TEST_RUN_TOKEN=""

LAST_OUTPUT=""
LAST_EXIT_CODE=0
LAST_COMMAND=""
TEST_MESSAGE=""
TEST_FAILURE=""

CLI_LABEL=""
CLI_BASE_COUNT=0
CLI_BASE_0=""
CLI_BASE_1=""
CLI_BASE_2=""
CLI_BASE_3=""
CLI_BASE_4=""
CLI_BASE_5=""
CLI_BASE_6=""
CLI_BASE_7=""

TEST_COUNT=0
RESULT_COUNT=0

PASS_COUNT=0
FAIL_COUNT=0
SKIP_COUNT=0
SELECTED_COUNT=0

USE_COLOR=0

TEST_IDS=()
TEST_CATEGORIES=()
TEST_DESCRIPTIONS=()
TEST_DESTRUCTIVE=()
TEST_FUNCTIONS=()

RESULT_IDS=()
RESULT_CATEGORIES=()
RESULT_DESCRIPTIONS=()
RESULT_DESTRUCTIVE=()
RESULT_STATUSES=()
RESULT_DURATIONS=()
RESULT_MESSAGES=()
RESULT_COMMANDS=()
RESULT_EXIT_CODES=()

setup_colors() {
  if [[ -t 1 && $JSON_REPORT -eq 0 ]]; then
    USE_COLOR=1
    return
  fi

  if [[ -t 2 && $JSON_REPORT -eq 1 ]]; then
    USE_COLOR=1
  fi
}

colorize() {
  local color="$1"
  local text="$2"

  if [[ $USE_COLOR -eq 0 ]]; then
    printf '%s' "$text"
    return
  fi

  printf '\033[%sm%s\033[0m' "$color" "$text"
}

log_line() {
  if [[ $JSON_REPORT -eq 1 ]]; then
    printf '%s\n' "$1" >&2
  else
    printf '%s\n' "$1"
  fi
}

log_info() {
  log_line "$(colorize 36 "$1")"
}

log_pass() {
  log_line "$(colorize 32 "$1")"
}

log_fail() {
  log_line "$(colorize 31 "$1")"
}

log_skip() {
  log_line "$(colorize 33 "$1")"
}

abort() {
  log_fail "$1"
  exit 1
}

print_usage() {
  cat <<EOF
usage: $(basename "$0") [--project <path>] [--filter <pattern>] [--destructive] [--dry-run] [--json]

options:
  --project <path>   Unity project root. Falls back to UNITY_PROJECT_PATH.
  --filter <text>    Run only tests whose id/category/description contains the text.
  --destructive      Include destructive tests such as delete/remove flows.
  --dry-run          Print the selected test list without executing commands.
  --json             Print the final report as JSON.
  -h, --help         Show this help.
EOF
}

canonicalize_dir() {
  local input="$1"
  (
    cd "$input" >/dev/null 2>&1 || exit 1
    pwd -P
  )
}

lowercase() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]'
}

compact_output() {
  local value="$1"
  value="${value//$'\r'/}"
  value="${value//$'\n'/\\n}"
  if [[ ${#value} -gt 500 ]]; then
    value="${value:0:497}..."
  fi
  printf '%s' "$value"
}

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}

set_cli_base() {
  CLI_BASE_COUNT=$#
  CLI_BASE_0="${1:-}"
  CLI_BASE_1="${2:-}"
  CLI_BASE_2="${3:-}"
  CLI_BASE_3="${4:-}"
  CLI_BASE_4="${5:-}"
  CLI_BASE_5="${6:-}"
  CLI_BASE_6="${7:-}"
  CLI_BASE_7="${8:-}"
}

build_command_array() {
  COMMAND_ARGS=()

  if [[ $CLI_BASE_COUNT -ge 1 ]]; then COMMAND_ARGS+=("$CLI_BASE_0"); fi
  if [[ $CLI_BASE_COUNT -ge 2 ]]; then COMMAND_ARGS+=("$CLI_BASE_1"); fi
  if [[ $CLI_BASE_COUNT -ge 3 ]]; then COMMAND_ARGS+=("$CLI_BASE_2"); fi
  if [[ $CLI_BASE_COUNT -ge 4 ]]; then COMMAND_ARGS+=("$CLI_BASE_3"); fi
  if [[ $CLI_BASE_COUNT -ge 5 ]]; then COMMAND_ARGS+=("$CLI_BASE_4"); fi
  if [[ $CLI_BASE_COUNT -ge 6 ]]; then COMMAND_ARGS+=("$CLI_BASE_5"); fi
  if [[ $CLI_BASE_COUNT -ge 7 ]]; then COMMAND_ARGS+=("$CLI_BASE_6"); fi
  if [[ $CLI_BASE_COUNT -ge 8 ]]; then COMMAND_ARGS+=("$CLI_BASE_7"); fi

  if [[ -n "$1" ]]; then
    COMMAND_ARGS+=(--project "$1")
  fi

  shift
  while [[ $# -gt 0 ]]; do
    COMMAND_ARGS+=("$1")
    shift
  done
}

find_built_dll() {
  local path=""
  local candidate=""

  for candidate in "$ROOT_DIR"/cli/UnityCli.Cli/bin/Debug/*/unity-cli.dll; do
    if [[ -f "$candidate" ]]; then
      path="$candidate"
      break
    fi
  done

  if [[ -z "$path" ]]; then
    for candidate in "$ROOT_DIR"/cli/UnityCli.Cli/bin/Release/*/unity-cli.dll; do
      if [[ -f "$candidate" ]]; then
        path="$candidate"
        break
      fi
    done
  fi

  printf '%s' "$path"
}

detect_cli() {
  local published_path="$ROOT_DIR/dist/unity-cli/unity-cli"
  local built_dll=""

  if [[ -n "${UNITY_CLI_BIN:-}" ]]; then
    if [[ ! -x "$UNITY_CLI_BIN" ]]; then
      abort "UNITY_CLI_BIN is not executable: $UNITY_CLI_BIN"
    fi

    set_cli_base "$UNITY_CLI_BIN"
    CLI_LABEL="$UNITY_CLI_BIN"
    return
  fi

  if [[ -x "$published_path" ]]; then
    set_cli_base "$published_path"
    CLI_LABEL="$published_path"
    return
  fi

  if ! command -v dotnet >/dev/null 2>&1; then
    abort "dotnet is required when dist/unity-cli/unity-cli is not available."
  fi

  built_dll="$(find_built_dll)"
  if [[ -z "$built_dll" ]]; then
    log_info "CLI build output is missing, so the runner will build cli/UnityCli.Cli once in Debug."
    dotnet build "$ROOT_DIR/cli/UnityCli.Cli/UnityCli.Cli.csproj" -c Debug -v q >/dev/null 2>&1 \
      || abort "Failed to build cli/UnityCli.Cli before running integration tests."
    built_dll="$(find_built_dll)"
  fi

  if [[ -n "$built_dll" ]]; then
    set_cli_base dotnet "$built_dll"
    CLI_LABEL="dotnet $built_dll"
    return
  fi

  set_cli_base dotnet run --verbosity quiet --project "$ROOT_DIR/cli/UnityCli.Cli" --
  CLI_LABEL="dotnet run --verbosity quiet --project $ROOT_DIR/cli/UnityCli.Cli --"
}

run_cli_capture() {
  local project_path="$1"
  shift

  build_command_array "$project_path" "$@"

  printf -v LAST_COMMAND '%q ' "${COMMAND_ARGS[@]}"
  LAST_COMMAND="${LAST_COMMAND% }"

  if LAST_OUTPUT="$("${COMMAND_ARGS[@]}" 2>&1)"; then
    LAST_EXIT_CODE=0
  else
    LAST_EXIT_CODE=$?
  fi
}

should_retry_live_error() {
  if [[ $LAST_EXIT_CODE -eq 0 ]]; then
    return 1
  fi

  if [[ "$LAST_OUTPUT" != *"status: error"* ]]; then
    return 1
  fi

  if [[ "$LAST_OUTPUT" != *"retryable: true"* ]]; then
    return 1
  fi

  [[ "$LAST_OUTPUT" == *"errorCode: LIVE_UNAVAILABLE"* || "$LAST_OUTPUT" == *"errorCode: REQUEST_CANCELLED"* ]]
}

run_cli_capture_with_retry() {
  local project_path="$1"
  shift

  local attempt=0
  local max_attempts=2

  while true; do
    run_cli_capture "$project_path" "$@"

    if [[ -z "$project_path" || "$project_path" != "$PROJECT_PATH" ]]; then
      return 0
    fi

    if ! should_retry_live_error; then
      return 0
    fi

    attempt=$((attempt + 1))
    if [[ $attempt -ge $max_attempts ]]; then
      return 0
    fi

    wait_for_live_ready 20 1 >/dev/null 2>&1 || return 0
  done
}

run_cli_main() {
  run_cli_capture_with_retry "$PROJECT_PATH" "$@"
}

run_cli_main_timeout() {
  local timeout_ms="$1"
  shift
  run_cli_capture_with_retry "$PROJECT_PATH" "$@" --timeout-ms "$timeout_ms"
}

run_cli_project() {
  local project_path="$1"
  shift
  run_cli_capture "$project_path" "$@"
}

run_cli_no_project() {
  run_cli_capture "" "$@"
}

assert_success_response() {
  if [[ $LAST_EXIT_CODE -ne 0 ]]; then
    TEST_FAILURE="Expected exit code 0 but got $LAST_EXIT_CODE from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  if [[ "$LAST_OUTPUT" != *"status: success"* ]]; then
    TEST_FAILURE="Expected a success response from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  return 0
}

assert_error_response() {
  local expected_exit="$1"
  local expected_code="$2"

  if [[ $LAST_EXIT_CODE -ne $expected_exit ]]; then
    TEST_FAILURE="Expected exit code $expected_exit but got $LAST_EXIT_CODE from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  if [[ "$LAST_OUTPUT" != *"status: error"* ]]; then
    TEST_FAILURE="Expected an error response from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  if [[ "$LAST_OUTPUT" != *"errorCode: $expected_code"* ]]; then
    TEST_FAILURE="Expected errorCode $expected_code from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  return 0
}

assert_output_contains() {
  local needle="$1"

  if [[ "$LAST_OUTPUT" != *"$needle"* ]]; then
    TEST_FAILURE="Expected output to contain [$needle] from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  return 0
}

assert_output_not_contains() {
  local needle="$1"

  if [[ "$LAST_OUTPUT" == *"$needle"* ]]; then
    TEST_FAILURE="Expected output not to contain [$needle] from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  return 0
}

assert_output_matches() {
  local pattern="$1"

  if ! printf '%s\n' "$LAST_OUTPUT" | grep -Eq "$pattern"; then
    TEST_FAILURE="Expected output to match /$pattern/ from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  return 0
}

assert_collapsed_vector() {
  local name="$1"
  local x="$2"
  local y="$3"
  local z="$4"
  local collapsed
  local pattern
  collapsed="$(printf '%s' "$LAST_OUTPUT" | tr -d '[:space:]')"
  pattern="\"${name}\":\\{\"x\":${x}(\\.0+)?,\"y\":${y}(\\.0+)?,\"z\":${z}(\\.0+)?\\}"
  if ! printf '%s\n' "$collapsed" | grep -Eq "$pattern"; then
    TEST_FAILURE="Expected ${name} (${x},${y},${z}) from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  return 0
}

extract_first_match() {
  local expression="$1"
  printf '%s\n' "$LAST_OUTPUT" | sed -nE "s/$expression/\\1/p" | head -n 1
}

wait_for_live_ready() {
  local attempts="${1:-30}"
  local delay_seconds="${2:-1}"
  local index=0

  while [[ $index -lt $attempts ]]; do
    run_cli_capture "$PROJECT_PATH" status
    if [[ $LAST_EXIT_CODE -eq 0 && "$LAST_OUTPUT" == *"transport: live"* ]]; then
      return 0
    fi

    sleep "$delay_seconds"
    index=$((index + 1))
  done

  TEST_FAILURE="Unity bridge did not become reachable again. Last status output=$(compact_output "$LAST_OUTPUT")"
  return 1
}

wait_for_status_pattern() {
  local pattern="$1"
  local attempts="${2:-20}"
  local delay_seconds="${3:-1}"
  local index=0

  while [[ $index -lt $attempts ]]; do
    run_cli_capture "$PROJECT_PATH" status
    if [[ $LAST_EXIT_CODE -eq 0 ]] && printf '%s\n' "$LAST_OUTPUT" | grep -Eq "$pattern"; then
      return 0
    fi

    sleep "$delay_seconds"
    index=$((index + 1))
  done

  TEST_FAILURE="status never matched /$pattern/. Last status output=$(compact_output "$LAST_OUTPUT")"
  return 1
}

ensure_clean_git_workspace() {
  [[ -x /usr/bin/git ]] || abort "git is required for live IPC test cleanup."

  PROJECT_GIT_ROOT="$(git -C "$PROJECT_PATH" rev-parse --show-toplevel 2>/dev/null)" \
    || abort "Unity project must be a git repository before running live IPC tests: $PROJECT_PATH"
  PROJECT_GIT_ROOT="$(canonicalize_dir "$PROJECT_GIT_ROOT")" \
    || abort "Failed to resolve git root for Unity project: $PROJECT_PATH"

  if [[ "$PROJECT_GIT_ROOT" != "$PROJECT_PATH" ]]; then
    abort "Unity project path must match the git repo root for deterministic cleanup. project=$PROJECT_PATH gitRoot=$PROJECT_GIT_ROOT"
  fi

  if [[ -n "$(git -C "$PROJECT_PATH" status --porcelain --untracked-files=normal)" ]]; then
    abort "Unity project git worktree must be clean before running live IPC tests: $PROJECT_PATH"
  fi
}

ensure_fixture_root() {
  if [[ $FIXTURE_ROOT_CREATED -eq 1 ]]; then
    return 0
  fi

  run_cli_main asset mkdir --path "$FIXTURE_ROOT/Assets/Moved"
  assert_success_response || return 1

  run_cli_main asset mkdir --path "$FIXTURE_ROOT/Materials"
  assert_success_response || return 1

  run_cli_main asset mkdir --path "$FIXTURE_ROOT/Prefabs"
  assert_success_response || return 1

  run_cli_main refresh
  assert_success_response || return 1
  FIXTURE_ROOT_CREATED=1
  return 0
}

create_material_asset() {
  local material_path="$1"
  run_cli_main asset create --type material --path "$material_path"
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$material_path\"" || return 1
  return 0
}

write_file() {
  local path="$1"
  shift
  mkdir -p "$(dirname "$path")"
  printf '%s' "$*" >"$path"
}

detect_material_color_property() {
  local material_path="$1"

  run_cli_main material info --path "$material_path"
  assert_success_response || return 1

  if printf '%s\n' "$LAST_OUTPUT" | grep -Fq '"name": "_Color"'; then
    printf '%s' "_Color"
    return 0
  fi

  if printf '%s\n' "$LAST_OUTPUT" | grep -Fq '"name": "_BaseColor"'; then
    printf '%s' "_BaseColor"
    return 0
  fi

  TEST_FAILURE="Could not find a writable color property on $material_path. Output=$(compact_output "$LAST_OUTPUT")"
  return 1
}

scene_test_object_name() {
  local base_name="$1"
  local run_token="${TEST_RUN_TOKEN:-$$}"
  printf 'Test_%s_%s' "$base_name" "$run_token"
}

scene_test_object_path() {
  local object_name="$1"
  printf '/%s[0]' "$object_name"
  return 0
}

capture_original_active_scene_path() {
  if [[ -n "$ORIGINAL_ACTIVE_SCENE_PATH" ]]; then
    return 0
  fi

  run_cli_main status
  assert_success_response || return 1
  ORIGINAL_ACTIVE_SCENE_PATH="$(extract_first_match '^[[:space:]]*"activeScenePath": "([^"]*)".*$')"
  return 0
}

ensure_scene_test_path() {
  if [[ -n "$SCENE_TEST_PATH" ]]; then
    return 0
  fi

  SCENE_TEST_PATH="$SCENE_TEST_DEFAULT_PATH"

  if [[ ! -f "$PROJECT_PATH/$SCENE_TEST_PATH" ]]; then
    TEST_FAILURE="Expected tracked sample scene to exist: $SCENE_TEST_PATH"
    return 1
  fi

  if ! git -C "$PROJECT_PATH" ls-files --error-unmatch "$SCENE_TEST_PATH" >/dev/null 2>&1; then
    TEST_FAILURE="Expected scene fixture to be tracked in git: $SCENE_TEST_PATH"
    return 1
  fi

  return 0
}

open_scene_test_fixture() {
  ensure_scene_test_path || return 1

  run_cli_main scene open --path "$SCENE_TEST_PATH" --force
  assert_success_response || return 1
  assert_output_contains "\"opened\": true" || return 1
  assert_output_contains "\"activeScenePath\": \"$SCENE_TEST_PATH\"" || return 1
  log_info "Opened the shared sample scene fixture: $SCENE_TEST_PATH"
  return 0
}

ensure_scene_test_active() {
  local active_scene_path=""

  ensure_scene_test_path || return 1

  run_cli_main status
  assert_success_response || return 1
  active_scene_path="$(extract_first_match '^[[:space:]]*"activeScenePath": "([^"]*)".*$')"

  if [[ "$active_scene_path" == "$SCENE_TEST_PATH" ]]; then
    return 0
  fi

  open_scene_test_fixture
}

create_local_package_fixture() {
  mkdir -p "$LOCAL_PACKAGE_DIR"
  cat >"$LOCAL_PACKAGE_DIR/package.json" <<EOF
{
  "name": "$LOCAL_PACKAGE_NAME",
  "version": "0.0.1",
  "displayName": "Unity CLI Live IPC Fixture",
  "description": "Temporary local package for live IPC integration tests"
}
EOF
}

ensure_local_package_added() {
  if [[ $LOCAL_PACKAGE_ADDED -eq 1 ]]; then
    return 0
  fi

  create_local_package_fixture
  run_cli_main_timeout 120000 package add --name "$LOCAL_PACKAGE_DIR"
  assert_success_response || return 1
  assert_output_contains "\"name\": \"$LOCAL_PACKAGE_NAME\"" || return 1
  assert_output_contains "\"added\": true" || return 1
  LOCAL_PACKAGE_ADDED=1
  wait_for_live_ready 60 1 || return 1
  return 0
}

stop_play_mode_and_wait() {
  run_cli_main stop
  assert_success_response || return 1
  wait_for_live_ready 20 1 || return 1
  wait_for_status_pattern '"isPlaying": false' 20 1 || return 1
  return 0
}

open_temporary_scratch_scene() {
  local code_file="$TEMP_DIR/code/open-scratch-scene.cs"
  cat >"$code_file" <<'EOF'
UnityEditor.SceneManagement.EditorSceneManager.NewScene(
    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
    UnityEditor.SceneManagement.NewSceneMode.Single);
EOF

  run_cli_main execute --file "$code_file" --force
  assert_success_response || return 1
  assert_output_contains "\"success\": true" || return 1
  return 0
}

ensure_scene_test_detached() {
  ensure_scene_test_path || return 1
  open_temporary_scratch_scene || return 1
  wait_for_live_ready 20 1 || return 1
  return 0
}

add_camera_to_active_scene() {
  local camera_name="$1"
  local code_file="$TEMP_DIR/code/add-camera-${camera_name}.cs"
  cat >"$code_file" <<EOF
var go = new UnityEngine.GameObject("$camera_name");
go.AddComponent<UnityEngine.Camera>();
EOF

  run_cli_main execute --file "$code_file" --force
  assert_success_response || return 1
  assert_output_contains "\"success\": true" || return 1
  return 0
}

cleanup() {
  local previous_json="$JSON_REPORT"
  JSON_REPORT=1

  if [[ $DRY_RUN -eq 1 ]]; then
    if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
      rm -rf "$TEMP_DIR"
    fi

    JSON_REPORT="$previous_json"
    return
  fi

  if [[ -n "$PROJECT_PATH" ]]; then
    wait_for_live_ready 10 1 >/dev/null 2>&1 || true
    run_cli_main stop >/dev/null 2>&1 || true
    wait_for_live_ready 10 1 >/dev/null 2>&1 || true

    if [[ $LOCAL_PACKAGE_ADDED -eq 1 ]]; then
      run_cli_main_timeout 120000 package remove --name "$LOCAL_PACKAGE_NAME" --force >/dev/null 2>&1 || true
      wait_for_live_ready 60 1 >/dev/null 2>&1 || true
    fi

    if [[ -n "$PROJECT_GIT_ROOT" ]]; then
      open_temporary_scratch_scene >/dev/null 2>&1 || true
      wait_for_live_ready 20 1 >/dev/null 2>&1 || true

      git -C "$PROJECT_PATH" checkout -- . >/dev/null 2>&1 || true
      git -C "$PROJECT_PATH" clean -fd >/dev/null 2>&1 || true
      run_cli_main refresh >/dev/null 2>&1 || true
      wait_for_live_ready 20 1 >/dev/null 2>&1 || true
      if [[ -n "$ORIGINAL_ACTIVE_SCENE_PATH" && "$ORIGINAL_ACTIVE_SCENE_PATH" == Assets/*.unity ]]; then
        run_cli_main scene open --path "$ORIGINAL_ACTIVE_SCENE_PATH" --force >/dev/null 2>&1 || true
      fi
    fi

    run_cli_no_project instances use "$PROJECT_PATH" >/dev/null 2>&1 || true
  fi

  if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
    rm -rf "$TEMP_DIR"
  fi

  JSON_REPORT="$previous_json"
}

register_test() {
  TEST_IDS+=("$1")
  TEST_CATEGORIES+=("$2")
  TEST_DESCRIPTIONS+=("$3")
  TEST_DESTRUCTIVE+=("$4")
  TEST_FUNCTIONS+=("$5")
  TEST_COUNT=$((TEST_COUNT + 1))
}

matches_filter() {
  local id="$1"
  local category="$2"
  local haystack=""
  local needle=""

  if [[ -z "$FILTER_PATTERN" ]]; then
    return 0
  fi

  haystack="$(lowercase "$id $category")"
  needle="$(lowercase "$FILTER_PATTERN")"

  [[ "$haystack" == *"$needle"* ]]
}

record_result() {
  RESULT_IDS+=("$1")
  RESULT_CATEGORIES+=("$2")
  RESULT_DESCRIPTIONS+=("$3")
  RESULT_DESTRUCTIVE+=("$4")
  RESULT_STATUSES+=("$5")
  RESULT_DURATIONS+=("$6")
  RESULT_MESSAGES+=("$7")
  RESULT_COMMANDS+=("$8")
  RESULT_EXIT_CODES+=("$9")
  RESULT_COUNT=$((RESULT_COUNT + 1))
}

test_local_status_live() {
  run_cli_main status
  assert_success_response || return 1
  assert_output_contains "transport: live" || return 1
  assert_output_contains "\"projectRoot\": \"$PROJECT_PATH\"" || return 1
  assert_output_matches '"projectHash": "[0-9a-f]{12}"' || return 1
  TEST_MESSAGE="status returned live project metadata"
}

test_local_instances_list() {
  run_cli_main instances list
  assert_success_response || return 1
  assert_output_contains "transport: cli" || return 1
  assert_output_contains "\"projectRoot\": \"$PROJECT_PATH\"" || return 1
  assert_output_contains "\"pipeName\":" || return 1
  TEST_MESSAGE="instances list showed the active project entry"
}

test_local_instances_use_path() {
  run_cli_no_project instances use "$PROJECT_PATH"
  assert_success_response || return 1
  assert_output_contains "transport: cli" || return 1
  assert_output_contains "\"projectRoot\": \"$PROJECT_PATH\"" || return 1
  assert_output_matches '"activeProjectHash": "[0-9a-f]{12}"' || return 1
  TEST_MESSAGE="instances use pinned the current project by path"
}

test_local_doctor() {
  run_cli_main doctor
  assert_success_response || return 1
  assert_output_contains "transport: cli" || return 1
  assert_output_contains "\"projectRoot\": \"$PROJECT_PATH\"" || return 1
  assert_output_contains "\"liveReachable\": true" || return 1
  TEST_MESSAGE="doctor reported a live reachable editor"
}

test_local_live_unavailable_fast_fail() {
  mkdir -p "$OFFLINE_PROJECT_PATH"

  run_cli_no_project instances use "$OFFLINE_PROJECT_PATH"
  assert_success_response || return 1

  run_cli_project "$OFFLINE_PROJECT_PATH" asset types
  assert_error_response 1 LIVE_UNAVAILABLE || return 1
  assert_output_contains "retryable: true" || return 1

  run_cli_no_project instances use "$PROJECT_PATH"
  assert_success_response || return 1
  TEST_MESSAGE="offline target returned LIVE_UNAVAILABLE without hanging"
}

test_diagnostics_raw_status() {
  run_cli_main raw --json '{"command":"status","arguments":{}}'
  assert_success_response || return 1
  assert_output_contains "transport: live" || return 1
  assert_output_contains "\"projectRoot\": \"$PROJECT_PATH\"" || return 1
  TEST_MESSAGE="raw status forwarded a live envelope"
}

test_asset_types() {
  run_cli_main asset types
  assert_success_response || return 1
  assert_output_contains "\"typeId\": \"material\"" || return 1
  assert_output_contains "\"typeId\": \"scene\"" || return 1
  TEST_MESSAGE="asset types listed built-in descriptors"
}

test_asset_mkdir() {
  local folder_path="$FIXTURE_ROOT/Folders/Nested/FolderOnly"
  run_cli_main asset mkdir --path "$folder_path"
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$folder_path\"" || return 1
  assert_output_contains "\"created\": true" || return 1
  TEST_MESSAGE="asset mkdir created the nested folder tree"
}

test_asset_create_material() {
  local material_path="$FIXTURE_ROOT/Assets/CreateMaterial.asset-check.mat"
  create_material_asset "$material_path" || return 1
  assert_output_contains "\"createdType\": \"material\"" || return 1
  TEST_MESSAGE="asset create material produced a .mat asset"
}

test_asset_info_path() {
  local material_path="$FIXTURE_ROOT/Assets/InfoByPath.mat"
  create_material_asset "$material_path" || return 1
  run_cli_main asset info --path "$material_path"
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$material_path\"" || return 1
  assert_output_contains "\"exists\": true" || return 1
  TEST_MESSAGE="asset info resolved metadata by path"
}

test_asset_info_guid() {
  local material_path="$FIXTURE_ROOT/Assets/InfoByGuid.mat"
  local guid=""
  create_material_asset "$material_path" || return 1

  run_cli_main asset info --path "$material_path"
  assert_success_response || return 1
  guid="$(extract_first_match '.*"guid": "([^"]+)".*')"
  if [[ -z "$guid" ]]; then
    TEST_FAILURE="Failed to extract guid from asset info output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi

  run_cli_main asset info --guid "$guid"
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$material_path\"" || return 1
  TEST_MESSAGE="asset info resolved the same asset by guid"
}

test_asset_find() {
  local asset_name="FindableMaterial"
  local material_path="$FIXTURE_ROOT/Assets/${asset_name}.mat"
  create_material_asset "$material_path" || return 1

  run_cli_main asset find --name "$asset_name" --folder "$FIXTURE_ROOT" --limit 5
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$material_path\"" || return 1
  TEST_MESSAGE="asset find returned the created material"
}

test_asset_reimport() {
  local material_path="$FIXTURE_ROOT/Assets/ReimportTarget.mat"
  create_material_asset "$material_path" || return 1

  run_cli_main asset reimport --path "$material_path"
  assert_success_response || return 1
  assert_output_contains "\"reimported\": true" || return 1
  assert_output_contains "\"path\": \"$material_path\"" || return 1
  TEST_MESSAGE="asset reimport refreshed the target asset"
}

test_asset_move() {
  local source_path="$FIXTURE_ROOT/Assets/MoveSource.mat"
  local target_path="$FIXTURE_ROOT/Assets/Moved/MoveResult.mat"
  create_material_asset "$source_path" || return 1

  run_cli_main asset move --from "$source_path" --to "$target_path"
  assert_success_response || return 1
  assert_output_contains "\"previousPath\": \"$source_path\"" || return 1
  assert_output_contains "\"path\": \"$target_path\"" || return 1
  TEST_MESSAGE="asset move relocated the material"
}

test_asset_rename() {
  local source_path="$FIXTURE_ROOT/Assets/RenameSource.mat"
  local renamed_path="$FIXTURE_ROOT/Assets/RenameResult.mat"
  create_material_asset "$source_path" || return 1

  run_cli_main asset rename --path "$source_path" --name "RenameResult"
  assert_success_response || return 1
  assert_output_contains "\"previousPath\": \"$source_path\"" || return 1
  assert_output_contains "\"path\": \"$renamed_path\"" || return 1
  TEST_MESSAGE="asset rename changed the file name in place"
}

test_asset_delete() {
  local material_path="$FIXTURE_ROOT/Assets/DeleteTarget.mat"
  create_material_asset "$material_path" || return 1

  run_cli_main asset delete --path "$material_path" --force
  assert_success_response || return 1
  assert_output_contains "\"deleted\": true" || return 1
  assert_output_contains "\"exists\": false" || return 1
  TEST_MESSAGE="asset delete removed the target asset"
}

test_material_info() {
  local material_path="$FIXTURE_ROOT/Materials/Inspectable.mat"
  create_material_asset "$material_path" || return 1

  run_cli_main material info --path "$material_path"
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$material_path\"" || return 1
  assert_output_contains "\"properties\": [" || return 1
  TEST_MESSAGE="material info returned shader properties"
}

test_material_set_property() {
  local material_path="$FIXTURE_ROOT/Materials/Settable.mat"
  local property_name=""
  create_material_asset "$material_path" || return 1

  property_name="$(detect_material_color_property "$material_path")" || return 1

  run_cli_main material set --path "$material_path" --property "$property_name" --value "0.25,0.5,0.75,1"
  assert_success_response || return 1
  assert_output_contains "\"property\": \"$property_name\"" || return 1
  assert_output_contains "\"newValue\": \"0.25,0.5,0.75,1\"" || return 1
  TEST_MESSAGE="material set updated a color property"
}

test_scene_open() {
  ensure_scene_test_path || return 1
  open_temporary_scratch_scene || return 1

  run_cli_main scene open --path "$SCENE_TEST_PATH"
  assert_success_response || return 1
  assert_output_contains "\"opened\": true" || return 1
  assert_output_contains "\"activeScenePath\": \"$SCENE_TEST_PATH\"" || return 1
  TEST_MESSAGE="scene open reopened SampleScene"
}

test_scene_inspect() {
  local object_name=""
  local object_path=""
  ensure_scene_test_path || return 1
  open_temporary_scratch_scene || return 1

  object_name="$(scene_test_object_name "InspectRoot")"
  object_path="$(scene_test_object_path "$object_name")"

  run_cli_main scene add-object --path "$SCENE_TEST_PATH" --name "$object_name"
  assert_success_response || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --with-values
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$object_path\"" || return 1
  TEST_MESSAGE="scene inspect reported the created hierarchy in SampleScene"
}

test_scene_inspect_node_filter() {
  local spec_path="$TEMP_DIR/specs/scene-inspect-node-filter.json"
  local root_name=""
  local sibling_name=""
  local root_path=""
  local child_path=""
  local grandchild_path=""
  local sibling_path=""
  ensure_scene_test_path || return 1
  open_temporary_scratch_scene || return 1

  root_name="$(scene_test_object_name "InspectNodeRoot")"
  sibling_name="$(scene_test_object_name "InspectNodeSibling")"
  root_path="$(scene_test_object_path "$root_name")"
  child_path="$root_path/Child[0]"
  grandchild_path="$child_path/Grandchild[0]"
  sibling_path="$(scene_test_object_path "$sibling_name")"

  cat >"$spec_path" <<EOF
{
  "version": 1,
  "operations": [
    {
      "op": "add-gameobject",
      "parent": "/",
      "node": {
        "name": "$root_name",
        "children": [
          {
            "name": "Child",
            "children": [
              {
                "name": "Grandchild",
                "components": [
                  { "type": "UnityEngine.BoxCollider" }
                ]
              }
            ]
          }
        ]
      }
    },
    {
      "op": "add-gameobject",
      "parent": "/",
      "node": {
        "name": "$sibling_name"
      }
    }
  ]
}
EOF

  run_cli_main scene patch --path "$SCENE_TEST_PATH" --spec-file "$spec_path"
  assert_success_response || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --node "$child_path" --with-values --max-depth 1 --omit-defaults
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$child_path\"" || return 1
  assert_output_contains "\"path\": \"$grandchild_path\"" || return 1
  assert_output_contains "\"type\": \"UnityEngine.BoxCollider\"" || return 1
  assert_output_not_contains "\"path\": \"$root_path\"" || return 1
  assert_output_not_contains "\"path\": \"$sibling_path\"" || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --node "/"
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$root_path\"" || return 1
  assert_output_contains "\"path\": \"$sibling_path\"" || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --node "   "
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$root_path\"" || return 1
  assert_output_contains "\"path\": \"$sibling_path\"" || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --node "/MissingNode[0]"
  assert_error_response 1 SCENE_NODE_NOT_FOUND || return 1
  TEST_MESSAGE="scene inspect --node filtered subtrees and treated root sentinels as full traversal"
}

test_scene_patch() {
  local spec_path="$TEMP_DIR/specs/scene-patch-basic.json"
  local object_name=""
  local object_path=""
  ensure_scene_test_path || return 1
  open_temporary_scratch_scene || return 1
  object_name="$(scene_test_object_name "PatchedRoot")"
  object_path="$(scene_test_object_path "$object_name")"

  cat >"$spec_path" <<EOF
{
  "version": 1,
  "operations": [
    {
      "op": "add-gameobject",
      "parent": "/",
      "node": {
        "name": "$object_name",
        "tag": "EditorOnly"
      }
    }
  ]
}
EOF

  run_cli_main scene patch --path "$SCENE_TEST_PATH" --spec-file "$spec_path"
  assert_success_response || return 1
  assert_output_contains "\"patched\": true" || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --with-values
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$object_path\"" || return 1
  TEST_MESSAGE="scene patch applied the add-gameobject spec in SampleScene"
}

test_scene_add_object() {
  local object_name=""
  local object_path=""
  ensure_scene_test_path || return 1
  open_temporary_scratch_scene || return 1

  object_name="$(scene_test_object_name "CameraRig")"
  object_path="$(scene_test_object_path "$object_name")"

  run_cli_main scene add-object --path "$SCENE_TEST_PATH" --name "$object_name" --components "UnityEngine.Camera"
  assert_success_response || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --with-values
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$object_path\"" || return 1
  assert_output_contains "\"type\": \"UnityEngine.Camera\"" || return 1
  TEST_MESSAGE="scene add-object created a node and its component in SampleScene"
}

test_scene_set_transform() {
  local object_name=""
  local object_path=""
  ensure_scene_test_path || return 1
  open_temporary_scratch_scene || return 1

  object_name="$(scene_test_object_name "Mover")"
  object_path="$(scene_test_object_path "$object_name")"

  run_cli_main scene add-object --path "$SCENE_TEST_PATH" --name "$object_name"
  assert_success_response || return 1

  run_cli_main scene open --path "$SCENE_TEST_PATH"
  assert_success_response || return 1

  run_cli_main scene set-transform --node "$object_path" --position "1,2,3"
  assert_success_response || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --with-values
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$object_path\"" || return 1
  assert_output_matches '"x": 1(\.0+)?' || return 1
  assert_output_matches '"y": 2(\.0+)?' || return 1
  assert_output_matches '"z": 3(\.0+)?' || return 1

  run_cli_main scene set-transform --node "$object_path" --rotation "0,30,0"
  assert_success_response || return 1
  assert_output_contains '"rotation"' || return 1
  assert_output_not_contains '"position"' || return 1
  assert_output_not_contains '"scale"' || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --node "$object_path" --with-values
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$object_path\"" || return 1
  assert_collapsed_vector "localPosition" "1" "2" "3" || return 1
  assert_collapsed_vector "localScale" "1" "1" "1" || return 1
  TEST_MESSAGE="scene set-transform updated localPosition and left scale untouched for rotation-only"
}

test_scene_add_component() {
  local object_name=""
  local object_path=""
  ensure_scene_test_path || return 1
  open_temporary_scratch_scene || return 1

  object_name="$(scene_test_object_name "PhysicsNodeAdd")"
  object_path="$(scene_test_object_path "$object_name")"

  run_cli_main scene add-object --path "$SCENE_TEST_PATH" --name "$object_name"
  assert_success_response || return 1

  run_cli_main scene add-component --path "$SCENE_TEST_PATH" --node "$object_path" --type "UnityEngine.BoxCollider" --values '{"m_IsTrigger":true}'
  assert_success_response || return 1

  run_cli_main scene inspect --path "$SCENE_TEST_PATH" --with-values
  assert_success_response || return 1
  assert_output_contains "\"path\": \"$object_path\"" || return 1
  assert_output_contains "\"type\": \"UnityEngine.BoxCollider\"" || return 1
  assert_output_contains "\"m_IsTrigger\": true" || return 1
  TEST_MESSAGE="scene add-component attached a BoxCollider in SampleScene"
}

test_scene_remove_component() {
  local object_name=""
  local object_path=""
  ensure_scene_test_path || return 1
  open_temporary_scratch_scene || return 1

  object_name="$(scene_test_object_name "PhysicsNodeRemove")"
  object_path="$(scene_test_object_path "$object_name")"

  run_cli_main scene add-object --path "$SCENE_TEST_PATH" --name "$object_name" --components "UnityEngine.BoxCollider"
  assert_success_response || return 1

  run_cli_main scene remove-component --path "$SCENE_TEST_PATH" --node "$object_path" --type "UnityEngine.BoxCollider" --force
  assert_success_response || return 1

  run_cli_main scene remove-component --path "$SCENE_TEST_PATH" --node "$object_path" --type "UnityEngine.BoxCollider" --force
  assert_error_response 1 SCENE_COMPONENT_NOT_FOUND || return 1
  TEST_MESSAGE="scene remove-component removed the BoxCollider from SampleScene"
}

test_prefab_create() {
  local prefab_path="$FIXTURE_ROOT/Prefabs/CreatePrefab.prefab"
  local spec_path="$TEMP_DIR/specs/prefab-create.json"

  cat >"$spec_path" <<EOF
{
  "version": 1,
  "root": {
    "name": "CreatePrefabRoot",
    "children": [
      { "name": "Visual" },
      { "name": "Hitbox" }
    ]
  }
}
EOF

  run_cli_main prefab create --path "$prefab_path" --spec-file "$spec_path"
  assert_success_response || return 1
  assert_output_contains "\"created\": true" || return 1
  assert_output_contains "\"path\": \"$prefab_path\"" || return 1
  TEST_MESSAGE="prefab create wrote the structured prefab asset"
}

test_prefab_inspect() {
  local prefab_path="$FIXTURE_ROOT/Prefabs/InspectPrefab.prefab"
  local spec_path="$TEMP_DIR/specs/prefab-inspect-create.json"

  cat >"$spec_path" <<EOF
{
  "version": 1,
  "root": {
    "children": [
      { "name": "Visual" },
      { "name": "Hitbox" }
    ]
  }
}
EOF

  run_cli_main prefab create --path "$prefab_path" --spec-file "$spec_path"
  assert_success_response || return 1

  run_cli_main prefab inspect --path "$prefab_path" --with-values
  assert_success_response || return 1
  assert_output_contains "\"path\": \"/Hitbox[0]\"" || return 1
  TEST_MESSAGE="prefab inspect reported the expected child path"
}

test_prefab_inspect_node_filter() {
  local prefab_path="$FIXTURE_ROOT/Prefabs/InspectNodeFilter.prefab"
  local spec_path="$TEMP_DIR/specs/prefab-inspect-node-filter.json"

  cat >"$spec_path" <<EOF
{
  "version": 1,
  "root": {
    "children": [
      {
        "name": "Visual",
        "children": [
          {
            "name": "MeshRoot",
            "components": [
              { "type": "UnityEngine.BoxCollider" }
            ]
          }
        ]
      },
      { "name": "Hitbox" }
    ]
  }
}
EOF

  run_cli_main prefab create --path "$prefab_path" --spec-file "$spec_path"
  assert_success_response || return 1

  run_cli_main prefab inspect --path "$prefab_path" --node "/Visual[0]" --with-values --max-depth 1 --omit-defaults
  assert_success_response || return 1
  assert_output_contains "\"path\": \"/Visual[0]\"" || return 1
  assert_output_contains "\"path\": \"/Visual[0]/MeshRoot[0]\"" || return 1
  assert_output_contains "\"type\": \"UnityEngine.BoxCollider\"" || return 1
  assert_output_not_contains "\"path\": \"/Hitbox[0]\"" || return 1

  run_cli_main prefab inspect --path "$prefab_path" --node "/"
  assert_success_response || return 1
  assert_output_contains "\"path\": \"/Visual[0]\"" || return 1
  assert_output_contains "\"path\": \"/Hitbox[0]\"" || return 1

  run_cli_main prefab inspect --path "$prefab_path" --node "   "
  assert_success_response || return 1
  assert_output_contains "\"path\": \"/Visual[0]\"" || return 1
  assert_output_contains "\"path\": \"/Hitbox[0]\"" || return 1

  run_cli_main prefab inspect --path "$prefab_path" --node "/MissingNode[0]"
  assert_error_response 1 PREFAB_NODE_NOT_FOUND || return 1
  TEST_MESSAGE="prefab inspect --node used real prefab paths and root sentinels"
}

test_prefab_patch() {
  local prefab_path="$FIXTURE_ROOT/Prefabs/PatchPrefab.prefab"
  local create_spec="$TEMP_DIR/specs/prefab-patch-create.json"
  local patch_spec="$TEMP_DIR/specs/prefab-patch.json"

  cat >"$create_spec" <<EOF
{
  "version": 1,
  "root": {
    "children": [
      { "name": "Hitbox" }
    ]
  }
}
EOF

  cat >"$patch_spec" <<EOF
{
  "version": 1,
  "operations": [
    {
      "op": "add-component",
      "target": "/Hitbox[0]",
      "component": {
        "type": "UnityEngine.BoxCollider"
      }
    },
    {
      "op": "set-component-values",
      "target": "/Hitbox[0]",
      "componentType": "UnityEngine.BoxCollider",
      "values": {
        "m_IsTrigger": true
      }
    }
  ]
}
EOF

  run_cli_main prefab create --path "$prefab_path" --spec-file "$create_spec"
  assert_success_response || return 1

  run_cli_main prefab patch --path "$prefab_path" --spec-file "$patch_spec"
  assert_success_response || return 1
  assert_output_contains "\"patched\": true" || return 1

  run_cli_main prefab inspect --path "$prefab_path" --with-values
  assert_success_response || return 1
  assert_output_contains "\"type\": \"UnityEngine.BoxCollider\"" || return 1
  assert_output_contains "\"m_IsTrigger\": true" || return 1
  TEST_MESSAGE="prefab patch added and configured a BoxCollider"
}

test_editor_refresh() {
  run_cli_main refresh
  assert_success_response || return 1
  assert_output_contains "AssetDatabase.Refresh completed" || return 1
  TEST_MESSAGE="refresh completed through the live bridge"
}

test_editor_execute_menu() {
  run_cli_main execute-menu --path "Assets/Refresh"
  assert_success_response || return 1
  assert_output_contains "\"executed\": true" || return 1
  TEST_MESSAGE="execute-menu invoked Assets/Refresh"
}

test_editor_execute_code() {
  local code_file="$TEMP_DIR/code/execute-check.cs"
  cat >"$code_file" <<EOF
System.Console.WriteLine("LIVE_IPC_EXECUTE_STDOUT");
UnityEngine.Debug.Log("LIVE_IPC_EXECUTE_LOG");
EOF

  run_cli_main execute --file "$code_file" --force
  assert_success_response || return 1
  assert_output_contains "\"success\": true" || return 1
  assert_output_contains "LIVE_IPC_EXECUTE_STDOUT" || return 1
  assert_output_contains "LIVE_IPC_EXECUTE_LOG" || return 1
  TEST_MESSAGE="execute ran a temporary C# file in the editor"
}

test_console_read_recent_log() {
  local code_file="$TEMP_DIR/code/console-log.cs"
  local marker="LIVE_IPC_CONSOLE_$(date +%s)-$$"

  cat >"$code_file" <<EOF
UnityEngine.Debug.Log("$marker");
EOF

  run_cli_main execute --file "$code_file" --force
  assert_success_response || return 1
  sleep 1

  run_cli_main read-console --type log --limit 50
  assert_success_response || return 1
  assert_output_contains "$marker" || return 1
  TEST_MESSAGE="read-console returned the freshly written log entry"
}

test_execute_log_assertion() {
  local code_file="$TEMP_DIR/code/execute-assert.cs"
  local marker="ucb-216"

  cat >"$code_file" <<EOF
UnityEngine.Debug.LogAssertion("$marker");
EOF

  run_cli_main execute --file "$code_file" --force
  assert_success_response || return 1
  sleep 1

  run_cli_main read-console --type assert --limit 50
  assert_success_response || return 1
  assert_output_contains "$marker" || return 1
  assert_output_contains "\"type\": \"Assert\"" || return 1
  TEST_MESSAGE="execute keeps Debug.LogAssertion and read-console --type assert returns it"
}

test_console_read_error_includes_exception() {
  local code_file="$TEMP_DIR/code/console-exception.cs"
  local marker="ucb-208"

  cat >"$code_file" <<EOF
UnityEngine.Debug.LogException(new System.Exception("$marker"));
EOF

  run_cli_main execute --file "$code_file" --force
  assert_success_response || return 1
  sleep 1

  run_cli_main read-console --type error --limit 50
  assert_success_response || return 1
  assert_output_contains "$marker" || return 1
  assert_output_contains "\"type\": \"Exception\"" || return 1
  TEST_MESSAGE="read-console --type error includes a logged exception"
}

test_editor_screenshot_camera() {
  local camera_name=""
  ensure_scene_test_path || return 1

  camera_name="$(scene_test_object_name "LiveIpcShotCamera")"

  open_temporary_scratch_scene || return 1
  run_cli_main scene add-object --path "$SCENE_TEST_PATH" --name "$camera_name" --components "UnityEngine.Camera"
  assert_success_response || return 1

  ensure_scene_test_active || return 1
  run_cli_main screenshot --camera "$camera_name" --path "$SCREENSHOT_PATH" --width 64 --height 64
  assert_success_response || return 1
  assert_output_contains "\"savedPath\": \"$SCREENSHOT_PATH\"" || return 1
  if [[ ! -s "$SCREENSHOT_PATH" ]]; then
    TEST_FAILURE="Expected screenshot file to exist and be non-empty: $SCREENSHOT_PATH"
    return 1
  fi
  TEST_MESSAGE="screenshot captured a PNG from the created camera"
}

test_editor_play() {
  local camera_name=""
  ensure_scene_test_path || return 1

  camera_name="$(scene_test_object_name "PlayModeCamera")"

  open_temporary_scratch_scene || return 1
  run_cli_main scene add-object --path "$SCENE_TEST_PATH" --name "$camera_name" --components "UnityEngine.Camera"
  assert_success_response || return 1

  ensure_scene_test_active || return 1
  run_cli_main play
  assert_success_response || return 1
  assert_output_contains "\"isPlaying\": true" || return 1
  wait_for_status_pattern '"isPlaying": true' 20 1 || return 1
  stop_play_mode_and_wait || return 1
  TEST_MESSAGE="play entered Play Mode"
}

test_editor_pause() {
  local camera_name=""
  ensure_scene_test_path || return 1

  camera_name="$(scene_test_object_name "PauseModeCamera")"

  open_temporary_scratch_scene || return 1
  run_cli_main scene add-object --path "$SCENE_TEST_PATH" --name "$camera_name" --components "UnityEngine.Camera"
  assert_success_response || return 1

  ensure_scene_test_active || return 1
  run_cli_main play
  assert_success_response || return 1
  wait_for_status_pattern '"isPlaying": true' 20 1 || return 1

  run_cli_main pause
  assert_success_response || return 1
  assert_output_contains "\"isPaused\": true" || return 1
  wait_for_status_pattern '"isPaused": true' 20 1 || return 1
  stop_play_mode_and_wait || return 1
  TEST_MESSAGE="pause suspended Play Mode"
}

test_editor_stop() {
  local camera_name=""
  ensure_scene_test_path || return 1

  camera_name="$(scene_test_object_name "StopModeCamera")"

  open_temporary_scratch_scene || return 1
  run_cli_main scene add-object --path "$SCENE_TEST_PATH" --name "$camera_name" --components "UnityEngine.Camera"
  assert_success_response || return 1

  ensure_scene_test_active || return 1
  run_cli_main play
  assert_success_response || return 1
  wait_for_status_pattern '"isPlaying": true' 20 1 || return 1

  run_cli_main stop
  assert_success_response || return 1
  assert_output_contains "\"isPlaying\": false" || return 1
  wait_for_status_pattern '"isPlaying": false' 20 1 || return 1
  TEST_MESSAGE="stop exited Play Mode"
}

test_custom_missing_command() {
  run_cli_main custom "__missing_live_ipc_command__"
  assert_error_response 1 CUSTOM_COMMAND_NOT_FOUND || return 1
  assert_output_contains "transport: live" || return 1
  TEST_MESSAGE="custom surfaced a structured missing-command error"
}

test_package_list() {
  run_cli_main_timeout 120000 package list
  assert_success_response || return 1
  assert_output_contains "\"packages\": [" || return 1

  run_cli_main_timeout 120000 package list --filter "com.unity" --limit 1
  assert_success_response || return 1
  assert_output_contains "\"packages\": [" || return 1
  assert_output_contains "\"name\": \"com.unity" || return 1
  local unity_package_count
  unity_package_count="$(printf '%s\n' "$LAST_OUTPUT" | grep -oF '"name": "com.unity' | wc -l | tr -d ' ')"
  if [[ "$unity_package_count" != "1" ]]; then
    TEST_FAILURE="Expected package list --limit 1 to return one matching package, got $unity_package_count from: $LAST_COMMAND | output=$(compact_output "$LAST_OUTPUT")"
    return 1
  fi
  TEST_MESSAGE="package list returned installed packages and supported filter/limit"
}

test_package_search() {
  run_cli_main_timeout 120000 package search --query "com.unity"
  assert_success_response || return 1
  assert_output_contains "\"results\": [" || return 1
  TEST_MESSAGE="package search returned registry matches"
}

test_package_add_local() {
  create_local_package_fixture
  run_cli_main_timeout 120000 package add --name "$LOCAL_PACKAGE_DIR"
  assert_success_response || return 1
  assert_output_contains "\"name\": \"$LOCAL_PACKAGE_NAME\"" || return 1
  assert_output_contains "\"added\": true" || return 1
  LOCAL_PACKAGE_ADDED=1
  wait_for_live_ready 60 1 || return 1

  run_cli_main_timeout 120000 package list
  assert_success_response || return 1
  assert_output_contains "$LOCAL_PACKAGE_NAME" || return 1
  TEST_MESSAGE="package add installed the temporary local package"
}

test_package_remove_local() {
  ensure_local_package_added || return 1

  run_cli_main_timeout 120000 package remove --name "$LOCAL_PACKAGE_NAME" --force
  assert_success_response || return 1
  assert_output_contains "\"name\": \"$LOCAL_PACKAGE_NAME\"" || return 1
  assert_output_contains "\"removed\": true" || return 1
  LOCAL_PACKAGE_ADDED=0
  wait_for_live_ready 60 1 || return 1
  TEST_MESSAGE="package remove deleted the temporary local package"
}

test_profile_memory_top() {
  run_cli_main profile memory top --limit 5 --scope loaded
  assert_success_response || return 1
  assert_output_contains "\"totals\"" || return 1
  assert_output_contains "\"top\"" || return 1
  assert_output_contains "Editor-measured sizes" || return 1
  assert_output_contains "differ from player builds" || return 1

  run_cli_main profile memory top --type Mesh --limit 5 --scope loaded
  assert_success_response || return 1
  assert_output_contains "\"type\": \"Mesh\"" || return 1
  TEST_MESSAGE="profile memory top returns per-type totals and the largest objects"
}

test_editor_compile() {
  run_cli_main compile
  assert_success_response || return 1
  assert_output_contains "script compilation requested" || return 1
  wait_for_live_ready 60 1 || return 1
  TEST_MESSAGE="compile triggered a script compilation request and the bridge recovered"
}

register_all_tests() {
  register_test "local_status_live" "local" "status returns live project metadata" "no" "test_local_status_live"
  register_test "local_instances_list" "local" "instances list shows the active project entry" "no" "test_local_instances_list"
  register_test "local_instances_use_path" "local" "instances use pins the current project by path" "no" "test_local_instances_use_path"
  register_test "local_doctor" "local" "doctor reports live reachability" "no" "test_local_doctor"
  register_test "local_live_unavailable_fast_fail" "local" "offline target returns LIVE_UNAVAILABLE quickly" "no" "test_local_live_unavailable_fast_fail"

  register_test "diagnostics_raw_status" "diagnostics" "raw forwards a status envelope" "no" "test_diagnostics_raw_status"

  register_test "asset_types" "asset" "asset types lists built-in descriptors" "no" "test_asset_types"
  register_test "asset_mkdir" "asset" "asset mkdir creates nested folders" "no" "test_asset_mkdir"
  register_test "asset_create_material" "asset" "asset create material produces a .mat asset" "no" "test_asset_create_material"
  register_test "asset_info_path" "asset" "asset info resolves metadata by path" "no" "test_asset_info_path"
  register_test "asset_info_guid" "asset" "asset info resolves metadata by guid" "no" "test_asset_info_guid"
  register_test "asset_find" "asset" "asset find returns the created asset" "no" "test_asset_find"
  register_test "asset_reimport" "asset" "asset reimport refreshes a target asset" "no" "test_asset_reimport"
  register_test "asset_move" "asset" "asset move relocates a target asset" "no" "test_asset_move"
  register_test "asset_rename" "asset" "asset rename changes the file name" "no" "test_asset_rename"
  register_test "asset_delete" "asset" "asset delete removes a target asset" "yes" "test_asset_delete"

  register_test "material_info" "material" "material info returns shader properties" "no" "test_material_info"
  register_test "material_set_property" "material" "material set updates a writable property" "no" "test_material_set_property"

  register_test "scene_open" "scene" "scene open reloads Assets/Scenes/SampleScene.unity" "no" "test_scene_open"
  register_test "scene_inspect" "scene" "scene inspect reports a created hierarchy" "no" "test_scene_inspect"
  register_test "scene_inspect_node_filter" "scene" "scene inspect --node handles root sentinels and subtrees" "no" "test_scene_inspect_node_filter"
  register_test "scene_patch" "scene" "scene patch applies an add-gameobject spec" "no" "test_scene_patch"
  register_test "scene_add_object" "scene" "scene add-object creates a node with components" "no" "test_scene_add_object"
  register_test "scene_set_transform" "scene" "scene set-transform updates only the vectors that were passed" "no" "test_scene_set_transform"
  register_test "scene_add_component" "scene" "scene add-component attaches a BoxCollider" "no" "test_scene_add_component"
  register_test "scene_remove_component" "scene" "scene remove-component removes a BoxCollider" "yes" "test_scene_remove_component"

  register_test "prefab_create" "prefab" "prefab create writes a structured prefab" "no" "test_prefab_create"
  register_test "prefab_inspect" "prefab" "prefab inspect reports child paths" "no" "test_prefab_inspect"
  register_test "prefab_inspect_node_filter" "prefab" "prefab inspect --node handles root sentinels and subtrees" "no" "test_prefab_inspect_node_filter"
  register_test "prefab_patch" "prefab" "prefab patch adds and configures a BoxCollider" "no" "test_prefab_patch"

  register_test "editor_refresh" "editor" "refresh runs through the live bridge" "no" "test_editor_refresh"
  register_test "editor_execute_menu" "editor" "execute-menu invokes Assets/Refresh" "no" "test_editor_execute_menu"
  register_test "editor_execute_code" "editor" "execute runs a temporary C# file" "no" "test_editor_execute_code"
  register_test "console_read_recent_log" "console" "read-console returns a freshly written log" "no" "test_console_read_recent_log"
  register_test "console_read_error_includes_exception" "console" "read-console --type error includes Exception entries" "no" "test_console_read_error_includes_exception"
  register_test "execute_log_assertion" "console" "execute Debug.LogAssertion is returned by read-console --type assert" "no" "test_execute_log_assertion"
  register_test "editor_screenshot_camera" "editor" "screenshot captures a PNG from a camera added to SampleScene" "no" "test_editor_screenshot_camera"
  register_test "editor_play" "editor" "play enters Play Mode from SampleScene" "no" "test_editor_play"
  register_test "editor_pause" "editor" "pause suspends Play Mode from SampleScene" "no" "test_editor_pause"
  register_test "editor_stop" "editor" "stop exits Play Mode from SampleScene" "no" "test_editor_stop"
  register_test "custom_missing_command" "editor" "custom surfaces a structured missing-command error" "no" "test_custom_missing_command"

  register_test "package_list" "package" "package list returns the installed package set" "no" "test_package_list"
  register_test "package_search" "package" "package search returns registry matches" "no" "test_package_search"
  register_test "package_add_local" "package" "package add installs a temporary local package" "yes" "test_package_add_local"
  register_test "package_remove_local" "package" "package remove deletes a temporary local package" "yes" "test_package_remove_local"

  register_test "editor_compile" "editor" "compile triggers a script compilation request" "no" "test_editor_compile"

  register_test "profile_memory_top" "editor" "profile memory top lists the largest loaded objects" "no" "test_profile_memory_top"
}

list_selected_tests() {
  local index=0
  local id=""
  local category=""
  local description=""
  local destructive=""
  local marker=""

  while [[ $index -lt $TEST_COUNT ]]; do
    id="${TEST_IDS[$index]}"
    category="${TEST_CATEGORIES[$index]}"
    description="${TEST_DESCRIPTIONS[$index]}"
    destructive="${TEST_DESTRUCTIVE[$index]}"

    if matches_filter "$id" "$category" "$description"; then
      marker=""
      if [[ "$destructive" == "yes" && $ALLOW_DESTRUCTIVE -eq 0 ]]; then
        marker=" [skip: destructive]"
      elif [[ "$destructive" == "yes" ]]; then
        marker=" [destructive]"
      fi

      log_line "- [$category] $id$marker"
      log_line "  $description"
      SELECTED_COUNT=$((SELECTED_COUNT + 1))
    fi

    index=$((index + 1))
  done
}

run_tests() {
  local index=0
  local id=""
  local category=""
  local description=""
  local destructive=""
  local function_name=""
  local started_at=0
  local ended_at=0
  local duration=0
  local status=""
  local message=""

  while [[ $index -lt $TEST_COUNT ]]; do
    id="${TEST_IDS[$index]}"
    category="${TEST_CATEGORIES[$index]}"
    description="${TEST_DESCRIPTIONS[$index]}"
    destructive="${TEST_DESTRUCTIVE[$index]}"
    function_name="${TEST_FUNCTIONS[$index]}"

    if ! matches_filter "$id" "$category" "$description"; then
      index=$((index + 1))
      continue
    fi

    SELECTED_COUNT=$((SELECTED_COUNT + 1))

    if [[ "$destructive" == "yes" && $ALLOW_DESTRUCTIVE -eq 0 ]]; then
      SKIP_COUNT=$((SKIP_COUNT + 1))
      record_result "$id" "$category" "$description" "$destructive" "skipped" "0" "Skipped because --destructive was not provided." "" "0"
      log_skip "[SKIP] [$category] $id - $description"
      index=$((index + 1))
      continue
    fi

    log_info "[RUN ] [$category] $id - $description"
    TEST_MESSAGE=""
    TEST_FAILURE=""
    LAST_COMMAND=""
    LAST_OUTPUT=""
    LAST_EXIT_CODE=0
    started_at="$(date +%s)"

    wait_for_live_ready 20 1 || true
    if [[ -n "$TEST_FAILURE" ]]; then
      status="failed"
      ended_at="$(date +%s)"
      duration=$((ended_at - started_at))
      message="$TEST_FAILURE"
      FAIL_COUNT=$((FAIL_COUNT + 1))
      record_result "$id" "$category" "$description" "$destructive" "$status" "$duration" "$message" "$LAST_COMMAND" "$LAST_EXIT_CODE"
      log_fail "[FAIL] [$category] $id - $message"
      index=$((index + 1))
      continue
    fi

    if "$function_name"; then
      status="passed"
      ended_at="$(date +%s)"
      duration=$((ended_at - started_at))
      message="${TEST_MESSAGE:-Passed.}"
      PASS_COUNT=$((PASS_COUNT + 1))
      record_result "$id" "$category" "$description" "$destructive" "$status" "$duration" "$message" "$LAST_COMMAND" "$LAST_EXIT_CODE"
      log_pass "[PASS] [$category] $id - $message"
    else
      status="failed"
      ended_at="$(date +%s)"
      duration=$((ended_at - started_at))
      message="${TEST_FAILURE:-Test failed without a message.}"
      FAIL_COUNT=$((FAIL_COUNT + 1))
      record_result "$id" "$category" "$description" "$destructive" "$status" "$duration" "$message" "$LAST_COMMAND" "$LAST_EXIT_CODE"
      log_fail "[FAIL] [$category] $id - $message"
    fi

    index=$((index + 1))
  done
}

print_summary() {
  log_line ""
  log_line "Summary"
  log_line "  selected: $SELECTED_COUNT"
  log_line "  passed:   $PASS_COUNT"
  log_line "  failed:   $FAIL_COUNT"
  log_line "  skipped:  $SKIP_COUNT"
}

print_json_report() {
  local generated_at
  local index=0
  local comma=""

  generated_at="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

  printf '{\n'
  printf '  "generatedAt": "%s",\n' "$(json_escape "$generated_at")"
  printf '  "projectPath": "%s",\n' "$(json_escape "$PROJECT_PATH")"
  printf '  "cli": "%s",\n' "$(json_escape "$CLI_LABEL")"
  printf '  "filter": "%s",\n' "$(json_escape "$FILTER_PATTERN")"
  printf '  "destructive": %s,\n' "$([[ $ALLOW_DESTRUCTIVE -eq 1 ]] && printf 'true' || printf 'false')"
  printf '  "summary": {\n'
  printf '    "selected": %s,\n' "$SELECTED_COUNT"
  printf '    "passed": %s,\n' "$PASS_COUNT"
  printf '    "failed": %s,\n' "$FAIL_COUNT"
  printf '    "skipped": %s\n' "$SKIP_COUNT"
  printf '  },\n'
  printf '  "results": [\n'

  while [[ $index -lt $RESULT_COUNT ]]; do
    if [[ $index -gt 0 ]]; then
      comma=","
    else
      comma=""
    fi

    printf '%s    {\n' "$comma"
    printf '      "id": "%s",\n' "$(json_escape "${RESULT_IDS[$index]}")"
    printf '      "category": "%s",\n' "$(json_escape "${RESULT_CATEGORIES[$index]}")"
    printf '      "description": "%s",\n' "$(json_escape "${RESULT_DESCRIPTIONS[$index]}")"
    printf '      "destructive": %s,\n' "$([[ "${RESULT_DESTRUCTIVE[$index]}" == "yes" ]] && printf 'true' || printf 'false')"
    printf '      "status": "%s",\n' "$(json_escape "${RESULT_STATUSES[$index]}")"
    printf '      "durationSeconds": %s,\n' "${RESULT_DURATIONS[$index]}"
    printf '      "exitCode": %s,\n' "${RESULT_EXIT_CODES[$index]}"
    printf '      "message": "%s",\n' "$(json_escape "${RESULT_MESSAGES[$index]}")"
    printf '      "command": "%s"\n' "$(json_escape "${RESULT_COMMANDS[$index]}")"
    printf '    }'

    index=$((index + 1))
  done

  printf '\n  ]\n'
  printf '}\n'
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --project)
        [[ $# -ge 2 ]] || abort "--project requires a value."
        PROJECT_PATH="$2"
        shift 2
        ;;
      --filter)
        [[ $# -ge 2 ]] || abort "--filter requires a value."
        FILTER_PATTERN="$2"
        shift 2
        ;;
      --destructive)
        ALLOW_DESTRUCTIVE=1
        shift
        ;;
      --dry-run)
        DRY_RUN=1
        shift
        ;;
      --json)
        JSON_REPORT=1
        shift
        ;;
      -h|--help)
        print_usage
        exit 0
        ;;
      *)
        if [[ -z "$PROJECT_PATH" ]]; then
          PROJECT_PATH="$1"
          shift
        else
          abort "Unexpected argument: $1"
        fi
        ;;
    esac
  done
}

main() {
  parse_args "$@"
  setup_colors

  if [[ -z "$PROJECT_PATH" ]]; then
    abort "UNITY_PROJECT_PATH or --project <path> is required."
  fi

  if [[ ! -d "$PROJECT_PATH" ]]; then
    abort "Unity project path does not exist: $PROJECT_PATH"
  fi

  PROJECT_PATH="$(canonicalize_dir "$PROJECT_PATH")" || abort "Failed to resolve project path: $PROJECT_PATH"
  [[ -d "$PROJECT_PATH/Assets" ]] || abort "Project path does not look like a Unity project: $PROJECT_PATH"

  TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/unity-cli-live-ipc.XXXXXX")" || abort "Failed to create a temporary directory."
  mkdir -p "$TEMP_DIR/specs" "$TEMP_DIR/code" "$TEMP_DIR/artifacts" \
    || abort "Failed to prepare temporary fixture directories under: $TEMP_DIR"
  FIXTURE_ROOT="Assets/LiveIpcTests"
  TEST_RUN_TOKEN="$(date +%Y%m%d%H%M%S)_$$"
  OFFLINE_PROJECT_PATH="$TEMP_DIR/offline-project"
  SCREENSHOT_PATH="$TEMP_DIR/artifacts/screenshot.png"
  LOCAL_PACKAGE_DIR="$TEMP_DIR/local-package"

  trap cleanup EXIT

  detect_cli
  register_all_tests
  ensure_clean_git_workspace

  log_info "Unity project: $PROJECT_PATH"
  log_info "CLI command: $CLI_LABEL"

  if [[ $DRY_RUN -eq 1 ]]; then
    list_selected_tests
    if [[ $SELECTED_COUNT -eq 0 ]]; then
      abort "No tests matched the current filter."
    fi
    exit 0
  fi

  wait_for_live_ready 20 1 || abort "$TEST_FAILURE"
  capture_original_active_scene_path || abort "$TEST_FAILURE"

  ensure_fixture_root || abort "$TEST_FAILURE"
  ensure_scene_test_path || abort "$TEST_FAILURE"

  run_tests

  if [[ $SELECTED_COUNT -eq 0 ]]; then
    abort "No tests matched the current filter."
  fi

  if [[ $JSON_REPORT -eq 1 ]]; then
    print_json_report
  else
    print_summary
  fi

  if [[ $FAIL_COUNT -gt 0 ]]; then
    exit 1
  fi
}

main "$@"
