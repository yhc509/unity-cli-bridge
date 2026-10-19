# Live IPC Test Scenarios

`Unity CLI Bridge`의 현재 명령 표면을 기준으로 작성한 수동 검증 시나리오 모음이다. 목표는 `수동 서버 실행 없이 Unity Editor를 CLI로 제어`한다는 저장소 목표를 실제 Unity 프로젝트에서 다시 확인하는 것이다.

## 공통 사전 조건

- macOS arm64 환경에서 Unity Editor가 실행 중이어야 한다.
- 대상 Unity 프로젝트는 Bridge import/compile이 끝난 상태여야 한다.
- 대상 Unity 프로젝트는 git 저장소여야 하고 worktree가 clean 상태여야 한다.
- 프로젝트 실제 경로를 기준으로 실행한다.
- 예시 경로는 반드시 `pwd -P` 기준으로 바꾼다.

```bash
export REPO_ROOT="$(cd /path/to/unity-cli && pwd -P)"
export UNITY_PROJECT_PATH="$(cd /path/to/UnityProject && pwd -P)"
export UNITY_CLI="$REPO_ROOT/dist/unity-cli/unity-cli"
export LIVE_IPC_ROOT="Assets/LiveIpcTests"
export LIVE_IPC_SCENE="Assets/Scenes/SampleScene.unity"

# publish binary가 없으면 아래 형식으로 대체한다.
# dotnet run --project "$REPO_ROOT/cli/UnityCli.Cli" -- ...

# 샘플 프로젝트가 아직 git 저장소가 아니면 cleanup 검증 전에 먼저 초기화한다.
# cd "$UNITY_PROJECT_PATH"
# git init
# cat >.gitignore <<'EOF'
# Library/
# Temp/
# Logs/
# obj/
# UserSettings/
# *.csproj
# *.sln
# .vs/
# EOF
# git add -A && git commit -m "initial: empty Unity project with bridge package"

# scene 관련 검증은 샘플 프로젝트에 이미 있는
# "$LIVE_IPC_SCENE" 을 고정 fixture path로 재사용하는 기준이다.
# runner는 `asset create --type scene`을 호출하지 않고 필요할 때만
# `scene open`과 임시 scratch scene 전환을 조합해 watcher 충돌을 피한다.
```

## 시나리오 작성 규칙

- asset/material/prefab fixture는 고정 루트 `Assets/LiveIpcTests/...` 아래에 둔다.
- scene 관련 시나리오는 `Assets/Scenes/SampleScene.unity` 하나만 재사용한다.
- runner는 `$LIVE_IPC_SCENE`을 고정 fixture path로 유지하고, scene 파일 명령 전에는 watcher 충돌을 피하려고 임시 scratch scene을 active로 둔다.
- editor 제어가 필요한 시점에만 `scene open --path "$LIVE_IPC_SCENE"`으로 다시 연다.
- runner는 `asset create --type scene`을 호출하지 않는다.
- 테스트 간 구분은 scene 파일을 새로 만드는 대신 고유한 gameobject 이름으로 한다.
- 시나리오 간 순서 의존을 만들지 않는다.
- 파괴 연산은 시나리오 안에서 명시적으로 `--force`를 사용한다.
- 정리는 active `SampleScene` watcher 충돌을 피하려고 임시 unsaved scene으로 전환한 뒤 `git -C "$UNITY_PROJECT_PATH" checkout -- . && git -C "$UNITY_PROJECT_PATH" clean -fd` 기준으로 한다.
- 새 기능을 추가할 때는 같은 형식의 `### <ID> <title>` 섹션을 하나 더 추가한다.

### 시나리오 템플릿

- 사전 조건:
- 실행 명령:
- 기대 결과:
- 확인 방법:

## 로컬

### LOCAL-001 `status` live 상태 확인

- 사전 조건: `$UNITY_PROJECT_PATH` 프로젝트가 Unity Editor에서 열려 있고 Bridge가 활성화되어 있다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" status
```

- 기대 결과: `status: success`, `transport: live`, `projectRoot`와 `projectHash`가 출력된다.
- 확인 방법: 출력의 `data` 블록에서 `"projectRoot": "$UNITY_PROJECT_PATH"`와 `pipeName`, `unityVersion`, `activeScenePath`를 확인한다.

### LOCAL-002 `status` fallback 확인

- 사전 조건: 실제로 열려 있지 않은 임시 경로를 하나 만든다.
- 실행 명령:

```bash
mkdir -p /tmp/unity-cli-live-offline
"$UNITY_CLI" --project /tmp/unity-cli-live-offline status
```

- 기대 결과: 명령은 성공하지만 `transport: cli`로 fallback 한다.
- 확인 방법: `liveReachable`가 `false`이고 `registryPath`, `unityPath` 같은 로컬 진단 정보가 나오는지 확인한다.

### LOCAL-003 `instances list`

- 사전 조건: 대상 프로젝트가 열려 있어 registry에 현재 인스턴스가 기록되어 있다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" instances list
```

- 기대 결과: `status: success`, `transport: cli`, 현재 프로젝트 인스턴스가 목록에 포함된다.
- 확인 방법: `instances` 배열 안에 현재 `projectRoot`, `pipeName`, `projectHash`가 있는지 확인한다.

### LOCAL-004 `instances use <projectPath>`

- 사전 조건: `$UNITY_PROJECT_PATH`가 실제 Unity 프로젝트 루트다.
- 실행 명령:

```bash
"$UNITY_CLI" instances use "$UNITY_PROJECT_PATH"
```

- 기대 결과: 활성 타깃이 현재 프로젝트로 고정된다.
- 확인 방법: 응답의 `activeProjectHash`가 채워지고 `projectRoot`가 `$UNITY_PROJECT_PATH`인지 확인한다.

### LOCAL-005 `doctor`

- 사전 조건: 대상 프로젝트가 실행 중이다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" doctor
```

- 기대 결과: `status: success`, `transport: cli`, `liveReachable: true`가 나온다.
- 확인 방법: `registryPath`, `projectRoot`, `pipeName`, `liveReachable`, `unityPath`를 확인한다.

### LOCAL-006 live IPC 끊김 즉시 실패 확인

- 사전 조건: 오프라인 디렉터리를 하나 만들 수 있다.
- 실행 명령:

```bash
mkdir -p /tmp/unity-cli-live-offline
"$UNITY_CLI" instances use /tmp/unity-cli-live-offline
"$UNITY_CLI" --project /tmp/unity-cli-live-offline asset types
"$UNITY_CLI" instances use "$UNITY_PROJECT_PATH"
```

- 기대 결과: `asset types`는 오래 대기하지 않고 `LIVE_UNAVAILABLE`로 실패한다.
- 확인 방법: 실패 출력에 `errorCode: LIVE_UNAVAILABLE`, `retryable: true`가 있는지 확인한다.

## 진단

### DIAG-001 `raw`

- 사전 조건: 대상 프로젝트가 실행 중이다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" raw --json '{"command":"status","arguments":{}}'
```

- 기대 결과: `status`와 같은 live 응답이 반환된다.
- 확인 방법: `transport: live`, `projectRoot`, `projectHash`가 출력되는지 확인한다.

## 에디터 제어

### EDITOR-001 `refresh`

- 사전 조건: 대상 프로젝트가 실행 중이다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" refresh
```

- 기대 결과: `AssetDatabase.Refresh completed` 메시지가 반환된다.
- 확인 방법: `status: success`와 메시지 문자열을 확인한다.

### EDITOR-002 `compile`

- 사전 조건: 대상 프로젝트가 실행 중이다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" compile
```

- 기대 결과: compile 요청이 즉시 반환된다.
- 확인 방법: `script compilation requested`가 출력되고, 잠시 뒤 `status`가 다시 `transport: live`로 응답하는지 확인한다.

### EDITOR-003 `execute-menu`

- 사전 조건: Unity Editor가 GUI 모드로 떠 있다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" execute-menu --path "Assets/Refresh"
```

- 기대 결과: 메뉴 실행 결과가 성공으로 반환된다.
- 확인 방법: `data`에 `"executed": true`가 있는지 확인한다.

### EDITOR-004 `execute`

- 사전 조건: 임시 C# 파일을 하나 만들 수 있다.
- 실행 명령:

```bash
cat >/tmp/unity-cli-live-execute.cs <<'EOF'
System.Console.WriteLine("LIVE_IPC_EXECUTE_STDOUT");
UnityEngine.Debug.Log("LIVE_IPC_EXECUTE_LOG");
EOF

"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" execute --file /tmp/unity-cli-live-execute.cs --force
```

- 기대 결과: 코드가 실행되고 stdout/log가 `output`에 모인다.
- 확인 방법: 응답에 `success: true`, `LIVE_IPC_EXECUTE_STDOUT`, `LIVE_IPC_EXECUTE_LOG`가 있는지 확인한다.

### EDITOR-005 `custom` 없는 명령 확인

- 사전 조건: 프로젝트에 같은 이름의 custom command가 등록되어 있지 않다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" custom "__missing_live_ipc_command__"
```

- 기대 결과: 구조화된 live 에러가 반환된다.
- 확인 방법: `errorCode: CUSTOM_COMMAND_NOT_FOUND`를 확인한다.

### EDITOR-006 `screenshot --camera`

- 사전 조건: runner가 scratch scene에서 camera를 `$LIVE_IPC_SCENE`에 추가한 뒤 `$LIVE_IPC_SCENE`을 다시 active scene으로 열어 둔다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-object --path "$LIVE_IPC_SCENE" --name LiveIpcShotCamera_EDITOR_006 --components "UnityEngine.Camera"
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" screenshot --camera LiveIpcShotCamera_EDITOR_006 --path /tmp/unity-cli-live-shot.png --width 64 --height 64
```

- 기대 결과: PNG 파일이 저장된다.
- 확인 방법: 응답의 `savedPath`, `fileSizeBytes`를 확인하고 `/tmp/unity-cli-live-shot.png`가 실제로 생겼는지 확인한다.

### EDITOR-007 `play`

- 사전 조건: runner가 scratch scene에서 camera를 `$LIVE_IPC_SCENE`에 추가한 뒤 `$LIVE_IPC_SCENE`을 다시 active scene으로 열어 둔다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-object --path "$LIVE_IPC_SCENE" --name LiveIpcPlayModeCamera_EDITOR_007 --components "UnityEngine.Camera"
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" play
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" status
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" stop
```

- 기대 결과: Play Mode에 진입한다.
- 확인 방법: `play` 응답의 `isPlaying: true`와 뒤이은 `status`의 `isPlaying: true`를 확인한다.

### EDITOR-008 `pause`

- 사전 조건: runner가 scratch scene에서 camera를 `$LIVE_IPC_SCENE`에 추가한 뒤 `$LIVE_IPC_SCENE`을 다시 active scene으로 열어 둔다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-object --path "$LIVE_IPC_SCENE" --name LiveIpcPauseModeCamera_EDITOR_008 --components "UnityEngine.Camera"
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" play
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" pause
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" status
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" stop
```

- 기대 결과: Play Mode가 일시 정지된다.
- 확인 방법: `pause` 응답과 `status` 모두에서 `isPaused: true`를 확인한다.

### EDITOR-009 `stop`

- 사전 조건: runner가 scratch scene에서 camera를 `$LIVE_IPC_SCENE`에 추가한 뒤 `$LIVE_IPC_SCENE`을 다시 active scene으로 열어 둔다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-object --path "$LIVE_IPC_SCENE" --name LiveIpcStopModeCamera_EDITOR_009 --components "UnityEngine.Camera"
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" play
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" stop
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" status
```

- 기대 결과: Play Mode가 종료된다.
- 확인 방법: `stop`과 `status` 모두에서 `isPlaying: false`, `isPaused: false`를 확인한다.

## 콘솔

### CONSOLE-001 `read-console`

- 사전 조건: `execute`나 Editor UI에서 log를 하나 남길 수 있다.
- 실행 명령:

```bash
cat >/tmp/unity-cli-live-log.cs <<'EOF'
UnityEngine.Debug.Log("LIVE_IPC_CONSOLE_MARKER");
EOF

"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" execute --file /tmp/unity-cli-live-log.cs --force
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" read-console --type log --limit 50
```

- 기대 결과: 최근 log 목록이 반환된다.
- 확인 방법: `entries` 안에 `LIVE_IPC_CONSOLE_MARKER`가 있는지 확인한다.

## 에셋

### ASSET-001 `asset types`

- 사전 조건: 대상 프로젝트가 실행 중이다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset types
```

- 기대 결과: built-in asset create 타입 목록이 나온다.
- 확인 방법: `typeId` 목록에 `material`, `scene`, `prefab`, `scriptable-object`가 있는지 확인한다.

### ASSET-002 `asset mkdir`

- 사전 조건: 경로 `Assets/LiveIpcTests/ASSET-002/Folders/Nested`가 아직 없다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset mkdir --path Assets/LiveIpcTests/ASSET-002/Folders/Nested
```

- 기대 결과: 누락된 폴더가 생성된다.
- 확인 방법: `created: true`와 결과 `path`를 확인한다.

### ASSET-003 `asset create`

- 사전 조건: 대상 경로가 비어 있다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/ASSET-003/CreateMaterial.mat
```

- 기대 결과: `.mat` asset이 생성된다.
- 확인 방법: `createdType: material`, 결과 `path`, `exists: true`를 확인한다.

### ASSET-004 `asset info --path`

- 사전 조건: `ASSET-003`과 별개로 테스트용 asset 하나를 만든다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/ASSET-004/InfoByPath.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset info --path Assets/LiveIpcTests/ASSET-004/InfoByPath.mat
```

- 기대 결과: asset 메타데이터가 반환된다.
- 확인 방법: `guid`, `assetName`, `mainType`, `exists: true`를 확인한다.

### ASSET-005 `asset info --guid`

- 사전 조건: 먼저 `asset info --path`로 GUID를 얻는다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/ASSET-005/InfoByGuid.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset info --path Assets/LiveIpcTests/ASSET-005/InfoByGuid.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset info --guid <GUID_FROM_PREVIOUS_STEP>
```

- 기대 결과: 같은 asset 메타데이터가 다시 반환된다.
- 확인 방법: 두 번째 응답의 `path`가 첫 번째 asset 경로와 같은지 확인한다.

### ASSET-006 `asset find`

- 사전 조건: 고유한 이름의 asset을 하나 만든다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/ASSET-006/FindableMaterial.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset find --name FindableMaterial --folder Assets/LiveIpcTests/ASSET-006 --limit 5
```

- 기대 결과: 만든 asset이 검색된다.
- 확인 방법: 결과 배열에 대상 `path`가 있는지 확인한다.

### ASSET-007 `asset reimport`

- 사전 조건: reimport 대상 asset이 존재한다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/ASSET-007/ReimportTarget.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset reimport --path Assets/LiveIpcTests/ASSET-007/ReimportTarget.mat
```

- 기대 결과: reimport 성공 응답이 반환된다.
- 확인 방법: `reimported: true`와 대상 `path`를 확인한다.

### ASSET-008 `asset move`

- 사전 조건: move source asset이 존재한다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/ASSET-008/MoveSource.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset move --from Assets/LiveIpcTests/ASSET-008/MoveSource.mat --to Assets/LiveIpcTests/ASSET-008/Moved/MoveResult.mat
```

- 기대 결과: asset이 새 경로로 이동한다.
- 확인 방법: `previousPath`와 새 `path`를 확인한다.

### ASSET-009 `asset rename`

- 사전 조건: rename 대상 asset이 존재한다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/ASSET-009/RenameSource.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset rename --path Assets/LiveIpcTests/ASSET-009/RenameSource.mat --name RenameResult
```

- 기대 결과: 같은 폴더에서 파일명만 바뀐다.
- 확인 방법: `previousPath`와 결과 `path`가 예상 경로인지 확인한다.

### ASSET-010 `asset delete --force`

- 사전 조건: 삭제 대상 asset이 존재한다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/ASSET-010/DeleteTarget.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset delete --path Assets/LiveIpcTests/ASSET-010/DeleteTarget.mat --force
```

- 기대 결과: asset이 삭제된다.
- 확인 방법: `deleted: true`, `exists: false`를 확인한다.

## Scene

- 공통 사전 조건: `$LIVE_IPC_SCENE`은 샘플 프로젝트에 이미 들어 있는 tracked scene asset path다.
- runner는 scene file 기반 명령 전에는 임시 scratch scene을 active로 둬서 `SampleScene` watcher 충돌을 피한다.
- `SCENE-001`은 scratch scene에서 같은 `SampleScene`을 다시 여는 검증이다.
- object 이름은 `..._SCENE_00X`처럼 고유하게 잡아 테스트 간 충돌을 피한다.

### SCENE-001 `scene open`

- 사전 조건: active scene이 `$LIVE_IPC_SCENE`이 아닌 scratch scene이다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene open --path "$LIVE_IPC_SCENE"
```

- 기대 결과: `SampleScene`이 active scene으로 열린다.
- 확인 방법: `opened: true`, `activeScenePath`가 `$LIVE_IPC_SCENE`인지 확인한다.

### SCENE-002 `scene inspect --with-values`

- 사전 조건: inspect 대상 scene으로 `$LIVE_IPC_SCENE`을 재사용한다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-object --path "$LIVE_IPC_SCENE" --name InspectRoot_SCENE_002
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene inspect --path "$LIVE_IPC_SCENE" --with-values
```

- 기대 결과: hierarchy와 component/value 정보가 나온다.
- 확인 방법: `path: /InspectRoot_SCENE_002[0]`, `transform`, `components`, `children`가 보이는지 확인한다.

### SCENE-003 `scene patch`

- 사전 조건: patch 대상 scene으로 `$LIVE_IPC_SCENE`을 재사용한다.
- 실행 명령:

```bash
cat >/tmp/unity-cli-scene-patch.json <<'EOF'
{
  "version": 1,
  "operations": [
    {
      "op": "add-gameobject",
      "parent": "/",
      "node": {
        "name": "PatchedRoot_SCENE_003",
        "tag": "EditorOnly"
      }
    }
  ]
}
EOF

"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene patch --path "$LIVE_IPC_SCENE" --spec-file /tmp/unity-cli-scene-patch.json
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene inspect --path "$LIVE_IPC_SCENE" --with-values
```

- 기대 결과: patch spec이 적용된다.
- 확인 방법: `patched: true`와 `path: /PatchedRoot_SCENE_003[0]`를 확인한다.

### SCENE-004 `scene add-object`

- 사전 조건: 대상 scene으로 `$LIVE_IPC_SCENE`을 재사용한다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-object --path "$LIVE_IPC_SCENE" --name CameraRig_SCENE_004 --components "UnityEngine.Camera"
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene inspect --path "$LIVE_IPC_SCENE" --with-values
```

- 기대 결과: 오브젝트와 지정한 component가 생성된다.
- 확인 방법: `/CameraRig_SCENE_004[0]`과 `UnityEngine.Camera`가 출력되는지 확인한다.

### SCENE-005 `scene set-transform`

- 사전 조건: 위치를 바꿀 오브젝트를 `$LIVE_IPC_SCENE`에 만든다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-object --path "$LIVE_IPC_SCENE" --name Mover_SCENE_005
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene open --path "$LIVE_IPC_SCENE"
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene set-transform --node /Mover_SCENE_005[0] --position 1,2,3
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene inspect --path "$LIVE_IPC_SCENE" --with-values
```

- 기대 결과: 오브젝트의 localPosition이 바뀐다.
- 확인 방법: `/Mover_SCENE_005[0]` 아래 `localPosition`의 `x`, `y`, `z`가 1, 2, 3인지 확인한다.

### SCENE-006 `scene add-component`

- 사전 조건: component를 붙일 오브젝트를 `$LIVE_IPC_SCENE`에 만든다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-object --path "$LIVE_IPC_SCENE" --name PhysicsNode_SCENE_006
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-component --path "$LIVE_IPC_SCENE" --node /PhysicsNode_SCENE_006[0] --type UnityEngine.BoxCollider --values '{"m_IsTrigger":true}'
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene inspect --path "$LIVE_IPC_SCENE" --with-values
```

- 기대 결과: BoxCollider가 추가되고 값이 들어간다.
- 확인 방법: `UnityEngine.BoxCollider`와 `m_IsTrigger: true`를 확인한다.

### SCENE-007 `scene remove-component --force`

- 사전 조건: 제거할 component가 붙은 오브젝트를 `$LIVE_IPC_SCENE`에 만든다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene add-object --path "$LIVE_IPC_SCENE" --name PhysicsNode_SCENE_007 --components "UnityEngine.BoxCollider"
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene remove-component --path "$LIVE_IPC_SCENE" --node /PhysicsNode_SCENE_007[0] --type UnityEngine.BoxCollider --force
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" scene remove-component --path "$LIVE_IPC_SCENE" --node /PhysicsNode_SCENE_007[0] --type UnityEngine.BoxCollider --force
```

- 기대 결과: component가 제거된다.
- 확인 방법: 두 번째 호출이 `SCENE_COMPONENT_NOT_FOUND`로 실패하는지 확인한다.

## Prefab

### PREFAB-001 `prefab create`

- 사전 조건: prefab spec 파일을 만들 수 있다.
- 실행 명령:

```bash
cat >/tmp/unity-cli-prefab-create.json <<'EOF'
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

"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" prefab create --path Assets/LiveIpcTests/PREFAB-001/CreatePrefab.prefab --spec-file /tmp/unity-cli-prefab-create.json
```

- 기대 결과: prefab asset이 생성된다.
- 확인 방법: `created: true`와 결과 `path`를 확인한다.

### PREFAB-002 `prefab inspect --with-values`

- 사전 조건: inspect할 prefab을 먼저 만든다.
- 실행 명령:

```bash
cat >/tmp/unity-cli-prefab-inspect.json <<'EOF'
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

"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" prefab create --path Assets/LiveIpcTests/PREFAB-002/InspectPrefab.prefab --spec-file /tmp/unity-cli-prefab-inspect.json
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" prefab inspect --path Assets/LiveIpcTests/PREFAB-002/InspectPrefab.prefab --with-values
```

- 기대 결과: prefab hierarchy와 values가 출력된다.
- 확인 방법: `path: /Hitbox[0]`와 root/child 구조를 확인한다.

### PREFAB-003 `prefab patch`

- 사전 조건: patch 대상 prefab이 존재한다.
- 실행 명령:

```bash
cat >/tmp/unity-cli-prefab-base.json <<'EOF'
{
  "version": 1,
  "root": {
    "children": [
      { "name": "Hitbox" }
    ]
  }
}
EOF

cat >/tmp/unity-cli-prefab-patch.json <<'EOF'
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

"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" prefab create --path Assets/LiveIpcTests/PREFAB-003/PatchPrefab.prefab --spec-file /tmp/unity-cli-prefab-base.json
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" prefab patch --path Assets/LiveIpcTests/PREFAB-003/PatchPrefab.prefab --spec-file /tmp/unity-cli-prefab-patch.json
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" prefab inspect --path Assets/LiveIpcTests/PREFAB-003/PatchPrefab.prefab --with-values
```

- 기대 결과: patch가 적용된다.
- 확인 방법: `UnityEngine.BoxCollider`와 `m_IsTrigger: true`를 확인한다.

## 패키지

### PACKAGE-001 `package list`

- 사전 조건: 대상 프로젝트가 실행 중이다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" package list
```

- 기대 결과: 설치된 package 목록이 반환된다.
- 확인 방법: `packages` 배열이 있고 주요 패키지 이름들이 보이는지 확인한다.

### PACKAGE-002 `package search`

- 사전 조건: Unity Package Registry 검색이 가능한 네트워크 상태다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" package search --query com.unity
```

- 기대 결과: query와 일치하는 package가 반환된다.
- 확인 방법: `results` 배열이 비어 있지 않은지 확인한다.

### PACKAGE-003 `package add`

- 사전 조건: 임시 local package 디렉터리를 만들 수 있다.
- 실행 명령:

```bash
mkdir -p /tmp/unity-cli-live-package
cat >/tmp/unity-cli-live-package/package.json <<'EOF'
{
  "name": "com.yhc509.liveipc.fixture",
  "version": "0.0.1",
  "displayName": "Unity CLI Live IPC Fixture"
}
EOF

"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" package add --name /tmp/unity-cli-live-package
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" package list
```

- 기대 결과: local package가 manifest에 추가된다.
- 확인 방법: `added: true`와 `package list` 결과에 `com.yhc509.liveipc.fixture`가 있는지 확인한다.

### PACKAGE-004 `package remove --force`

- 사전 조건: `PACKAGE-003`의 local package가 추가되어 있다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" package remove --name com.yhc509.liveipc.fixture --force
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" package list
```

- 기대 결과: local package가 제거된다.
- 확인 방법: `removed: true`와 `package list` 결과에서 해당 package가 사라졌는지 확인한다.

## 머터리얼

### MATERIAL-001 `material info`

- 사전 조건: 테스트용 material asset이 존재한다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/MATERIAL-001/Inspectable.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" material info --path Assets/LiveIpcTests/MATERIAL-001/Inspectable.mat
```

- 기대 결과: shader와 property 목록이 반환된다.
- 확인 방법: `path`, `shader`, `properties`를 확인한다.

### MATERIAL-002 `material set`

- 사전 조건: writable color property를 가진 material asset이 존재한다.
- 실행 명령:

```bash
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" asset create --type material --path Assets/LiveIpcTests/MATERIAL-002/Settable.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" material info --path Assets/LiveIpcTests/MATERIAL-002/Settable.mat
"$UNITY_CLI" --project "$UNITY_PROJECT_PATH" material set --path Assets/LiveIpcTests/MATERIAL-002/Settable.mat --property <_Color_OR__BaseColor> --value 0.25,0.5,0.75,1
```

- 기대 결과: 지정한 property가 갱신된다.
- 확인 방법: 응답의 `property`, `previousValue`, `newValue`를 확인한다.

## 참고

- 사용자 설명에 있던 `console-log`라는 이름의 명령은 현재 구현 기준으로는 `read-console`이다.
- `scene add-object`, `scene add-component`, `scene remove-component`는 내부적으로 `scene patch`를 사용한다.
- `scene set-transform`, `scene assign-material`은 active scene에 직접 적용하는 전용 명령이다.
- `asset delete`, `scene remove-component`, `package remove`는 모두 파괴 연산이므로 `--force`를 유지한 채 검증한다.
- 자동 검증은 [`run-live-ipc-tests.sh`](/Users/yhjang/dev/unity/unity-cli/tests/integration/run-live-ipc-tests.sh)에서 수행할 수 있다.
