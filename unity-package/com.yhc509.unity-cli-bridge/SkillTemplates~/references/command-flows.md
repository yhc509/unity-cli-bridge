# Command Flows

## 실행 파일 찾기

- `UNITY_CLI_BIN`이 있으면 그것을 우선 사용한다.
- PATH에 `ucli` 또는 `unity-cli`가 있으면 그 경로를 사용한다.
- 둘 다 없으면 현재 작업 디렉터리나 상위 디렉터리의 `unity-cli` 실행 파일을 찾는다.
- repo 루트에서 작업 중이라면 `./scripts/publish-osx-arm64.sh`로 `dist/unity-cli/unity-cli`를 빌드할 수 있다.
- 그래도 없으면 설치가 필요하다고 보고 진행을 멈춘다. 반복 호출이 잦으면 `ucli` alias나 symlink를 추가한다.

## 프로젝트 결정

**모든 명령에 `--project`를 반드시 붙인다.** 생략하면 CLI가 현재 작업 디렉터리의 Unity 프로젝트, `instances use`로 고정한 핀, 또는 단일 live 인스턴스로 라우팅할 수 있으므로 자동화 작업에서는 항상 명시한다.

```bash
# 현재 디렉터리가 Unity 프로젝트라면
PROJECT="$(pwd -P)"

# 실행 중인 프로젝트 이름을 알고 있다면
PROJECT="<your-project>"

# unity-cli 레포에서 개발/테스트 중이라면
PROJECT="unity-cli-bridge-sample"

# 특정 프로젝트를 경로로 지정하려면
PROJECT="/path/to/YourProject"
```

여러 프로젝트가 동시에 열려 있을 때 확인:

```bash
ucli instances list --brief
```

- `--project`는 프로젝트 이름이나 전체 경로 모두 가능하다.
- macOS에서는 항상 `pwd -P`로 실제 경로를 사용한다.
- 반복 작업에서 기본 대상을 고정하고 싶다면 `ucli instances use <projectPath|projectName>`를 쓴다.
- 12자 hash 입력은 충돌 시 ambiguous 에러로 거부된다.

## 상태 확인

가장 먼저 아래 흐름을 기준으로 본다.

```bash
ucli status --project "$PROJECT" --output compact
```

중요하게 볼 값:

- `projectName` — **의도한 프로젝트가 맞는지 반드시 확인**
- `projectRoot`
- `isCompiling`
- `isUpdating`

> `--output compact`는 envelope 메타(status, transport, durationMs 등)를 제거하고 data payload만 반환한다. LLM이 소비하는 모든 명령에 기본으로 붙인다. 사람이 읽거나 전체 envelope이 필요하면 `--json`을 쓴다.

## 기본 운용

- 연결 확인: `status`, `refresh`
- 플레이 상태: `play`, `pause`, `stop`
- 로그 확인: `read-console`
- 메뉴 실행: `execute-menu`
- 메뉴 조회: `execute-menu --list`
- scene 전환: `scene open`
- 코드 실행 인자: `ucli execute --project "$PROJECT" --code 'Debug.Log(__pucArgsJson);' --args '{"k":"v"}' --force --output compact`

live 편집이 필요한 명령은 에디터가 켜져 있고 busy 상태가 아닐 때 우선 실행한다.

## 메뉴 실행 (execute-menu)

### 언제 메뉴 조회를 먼저 하는가

메뉴 경로는 Unity 버전마다 다르다. **경로를 추측하지 말고** 반드시 `--list`로 먼저 확인한다.

권장 사례:
- **UI 요소 추가 전**: Unity 6에서 `GameObject/UI/Button`은 없고 `GameObject/UI (Canvas)/Button - TextMeshPro`가 있다. 항상 조회 먼저.
- **첫 메뉴 실행 전**: 프로젝트에 연결한 뒤 한 번은 `--list "GameObject"`로 전체 메뉴 구조를 파악한다.
- **`executed: false` 반환 시**: 경로가 틀렸거나 validation 실패. `--list`로 올바른 경로를 확인한다.
- **커스텀 패키지 메뉴**: 패키지가 등록한 메뉴는 표준 경로와 다를 수 있으므로 항상 조회.

```bash
# 전체 GameObject 하위 메뉴 조회
ucli execute-menu --list "GameObject" --project "$PROJECT" --json

# UI 관련 메뉴만 조회
ucli execute-menu --list "GameObject/UI" --project "$PROJECT" --json

# 조회 결과에서 정확한 경로로 실행
ucli execute-menu --path "GameObject/UI (Canvas)/Button - TextMeshPro" --project "$PROJECT" --json
```

### execute-menu로 충분한 경우

3D Object, UI, Audio, Light, Effects 등 Unity 기본 메뉴에서 오브젝트를 추가하는 경우 별도 템플릿 커맨드 없이 `execute-menu`로 충분하다. 새 커맨드를 만들기 전에 메뉴로 해결 가능한지 먼저 확인한다.

## live 연결 전제

`instances list`, `instances use`, `doctor`를 제외한 명령은 실행 중인 Unity Editor와 bridge 연결이 필요하다.

- `status --json`에서 `liveReachable`이 false면 먼저 에디터를 열고 bridge import/compile 완료를 기다린다.
- `compile`, `refresh`, asset/material/package/scene/prefab 명령은 모두 live IPC로 실행한다.
- `play`, `pause`, `stop`, `read-console`, `execute-menu`, `execute`, `custom`, `screenshot`도 live 전용으로 본다.

## 에셋 작업 흐름

### 조회

```bash
ucli asset find --project "$PROJECT" --name Sample --folder Assets --limit 10 --output compact
ucli asset find --project "$PROJECT" --type Material --output compact
ucli asset types --project "$PROJECT" --output compact
ucli asset info --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --output compact
```

> **`--name`은 글로브 패턴이 아니라 Unity 검색 필터 문법의 텍스트 검색어다.** `Sample*` 같은 글로브가 아니라 `Sample`로 쓰면 Unity의 `AssetDatabase.FindAssets("Sample")`이 부분 매칭한다. `--type`만으로도 검색 가능하다 (예: `--type Scene`은 `FindAssets("t:Scene")`). `--name`과 `--type`을 함께 쓰면 `FindAssets("name t:Type")`으로 조합된다.

> **`--folder`**: 검색 범위를 특정 폴더로 제한한다. 기본값은 `Assets` 전체. 하위 폴더 에셋도 재귀적으로 포함된다. 예: `--folder Assets/Prefabs`는 Prefabs 폴더와 그 하위의 모든 에셋을 검색한다.

### 생성

```bash
ucli asset create --project "$PROJECT" --type material --path Assets/Materials/NewMaterial --output compact
ucli asset create --project "$PROJECT" --type scriptable-object --path Assets/Data/NewData --type-name MyNamespace.MyData --data-json '{"title":"hello"}' --output compact
```

### 안전 규칙

- 삭제는 항상 `--force`
- 이름 변경, 이동, 생성 덮어쓰기는 `--force`
- 먼저 `asset info`나 `asset find`로 대상이 맞는지 본다

## scene 작업 흐름

### 조회

```bash
# 기본 구조 확인 (깊이 제한 + 기본값 생략으로 토큰 절약)
ucli scene inspect --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --max-depth 2 --omit-defaults --output compact

# 특정 노드의 component 값까지 확인 (patch 전 필수). --with-values에는 항상 --omit-defaults를 붙여 기본값을 생략한다
ucli scene inspect --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --with-values --omit-defaults --output compact
```

### 오브젝트 추가

```bash
# 빈 GameObject 추가
ucli scene add-object --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --name MyObject --output compact

# 프리미티브 추가 (MeshFilter+MeshRenderer+Collider 자동 포함)
ucli scene add-object --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --name Floor --primitive Plane --parent "/Environment[0]" --position 0,0,0 --output compact
```

`--primitive`는 Cube, Sphere, Capsule, Cylinder, Plane, Quad를 지원한다. `--parent`와 `--position`을 함께 쓰면 한 번의 호출로 생성+배치가 완료되고, 응답에 `createdPath`가 포함되어 후속 inspect가 필요 없다.

### Transform 수정

```bash
ucli scene set-transform --project "$PROJECT" --node "/Environment[0]/Floor[0]" --position 3,0,0 --scale 2,2,2 --output compact
```

`--position`, `--rotation`, `--scale` 중 최소 하나를 지정한다. `scene patch --spec-json` 대신 이 편의 명령을 우선 사용한다.

### 머티리얼 할당

```bash
ucli scene assign-material --project "$PROJECT" --node "/Environment[0]/Floor[0]" --material Assets/Materials/MyMat.mat --output compact
```

노드의 MeshRenderer.sharedMaterials[0]에 머티리얼을 할당한다. `scene patch`로 `m_Materials.Array.data[0]`을 직접 지정하는 것보다 간편하다.

### 수정 (spec 기반)

```bash
ucli scene patch --project "$PROJECT" --path Assets/Scenes/SampleScene.unity --spec-file /tmp/scene-patch-basic.json --output compact
```

빠르게 시작할 때는 설치된 스킬의 `assets/scene-patch-basic.json`을 복사해서 `/tmp/scene-patch-basic.json`처럼 별도 파일로 수정한다.

### 안전 규칙

- 먼저 `scene inspect --with-values`로 GameObject path와 component field를 확인한다
- Rigidbody, Collider, Renderer, Light, Camera의 흔한 component value는 `mass`, `damping`, `isTrigger`, `materials[0]`, `shadowStrength`, `fieldOfView` 같은 friendly key를 쓸 수 있다
- `--omit-defaults` 결과는 read-only이다. patch input으로 그대로 쓰면 생략된 필드가 복원되지 않는다
- node path는 `/Root[0]/Child[0]` 형식으로 쓴다
- `/`는 virtual scene root이고, root GameObject 추가의 parent로만 쓴다
- 대상 scene이 이미 열려 있다면 먼저 저장하거나 변경을 버려서 clean 상태로 맞춘다
- `delete-gameobject`, `remove-component`, dirty scene을 버리는 `scene open`에는 `--force`가 필요하다

## 머티리얼 조회

```bash
# 전체 속성 조회
ucli material info --project "$PROJECT" --path Assets/Materials/MyMat.mat --output compact

# 기본값 생략 (토큰 절약, URP/Lit 48개 → 변경된 것만)
ucli material info --project "$PROJECT" --path Assets/Materials/MyMat.mat --omit-defaults --output compact
```

## 스크린샷

```bash
# Game View 캡처 — 기본값이 이미 jpg q75 + 1024px 축소다 (--view 생략 시 game이 기본)
ucli screenshot --project "$PROJECT" --path /tmp/capture.jpg --output compact

# lossless 원본이 필요할 때만
ucli screenshot --project "$PROJECT" --path /tmp/capture.png --format png --max-width 0 --output compact

# Scene View 캡처
ucli screenshot --project "$PROJECT" --path /tmp/scene.jpg --view scene --output compact
```

## 테스트 러너

`test list` / `test run` / `test results`로 Editor 재시작 없이 Test Runner를 트리거한다.

기본 조회와 실행:

```bash
ucli test list --project "$PROJECT" --mode all --no-detail --output compact
ucli test run --project "$PROJECT" --mode edit --filter PlayerControllerTests --failures-only --output compact
ucli test run --project "$PROJECT" --mode play --filter Smoke --wait --failures-only --output compact
ucli test results --project "$PROJECT" --run-id <runId> --failures-only --output compact
```

- `test list --mode all`은 EditMode와 PlayMode 테스트를 같이 나열한다
- `test run --mode edit`은 동기 응답으로 결과 payload를 바로 반환한다
- `test run --mode play`는 기본적으로 `STARTED`와 `runId`를 즉시 반환한다
- PlayMode 결과까지 한 번에 기다려야 하면 `--wait`를 붙여 CLI가 폴링하게 한다
- `--filter`는 test full name의 대소문자 무시 substring이다
- `test results --run-id <runId>`는 이미 시작된 run의 디스크 캐시나 진행 상태를 다시 읽는다
- `--failures-only`는 summary count를 유지하면서 `tests[]`만 non-passed로 줄인다
- `test list --no-detail`은 `fullName`과 `mode`만 반환한다

test run이 콜백 누락이나 Editor 이상으로 멈춰 락이 안 풀리면 `ucli test cancel --project "$PROJECT" --output compact`로 수동 해제한다. graceful `TestRunnerApi` cancel을 먼저 시도한 뒤 항상 락을 풀고, 취소된 `runId`를 반환한다. 진행 중인 run이 없으면 no-op 성공으로 반환된다.

PlayMode domain reload를 생략할 수 있는 경우:

```bash
ucli test run --project "$PROJECT" --mode play --filter Smoke --wait --no-domain-reload --failures-only --output compact
```

- `--no-domain-reload`는 PlayMode 전용 속도 옵션이다
- static state leakage 위험이 있으므로 테스트가 자체 reset을 보장할 때만 쓴다
- EditMode와 함께 쓰면 usage error다

AI repair loop는 짧게 유지한다:

```bash
ucli refresh --project "$PROJECT" --output compact
ucli test run --project "$PROJECT" --mode edit --filter <related-name> --failures-only --output compact
```

실패하면 failing test name, message, stack trace를 기준으로 수정하고 같은 filter로 재실행한다. non-`Completed` 결과는 envelope `status: error`와 CLI exit code 1로 반환된다.

## 검증 루틴

live 작업 뒤 기본 검증:

```bash
# --type 생략 시 error/warning/log를 한 번에 반환 — 호출을 2회로 나눌 필요 없다
ucli read-console --project "$PROJECT" --limit 10 --no-stacktrace --output compact
# 특정 타입만 필요할 때만 좁힌다
ucli read-console --project "$PROJECT" --type error --limit 10 --no-stacktrace --output compact
```

에러나 경고가 있으면 성공으로 바로 닫지 않는다.
