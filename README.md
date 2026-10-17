# Fire3D Unity

Runtime Android cho tập huấn sơ tán FET3D: IFC → 3D → Unity Android → QR → Training → Result.

## Trạng thái

Runtime đã chạy được trong Editor: mô phỏng lửa/khói/nhiệt và NPC bằng **Entities**, camera third-person có xử lý va chạm, tương tác theo ngữ cảnh (tối đa 3 nút, nút giữ, bẫy), HUD, phiên tập huấn với telemetry đúng shape backend, bridge Mobile, nạp gói AssetBundle có ánh sáng bake, worker build trên server và agent pull-mode cho VM (đã chạy end-to-end với fake backend). Chưa nghiệm thu trên thiết bị Android thật, chưa chạy agent trên VM với BE thật và chưa có GLB thật từ Blender worker.

## Môi trường

- Unity **6000.6.6f1** (Android Build Support + SDK/NDK + OpenJDK), Git LFS (jpg/png/glb/exr…).
- Package chính: URP 17.6, Entities 6.6, Burst, Collections, Mathematics, Input System, uGUI, Newtonsoft, glTFast (chỉ dùng trong Editor để import GLB), Test Framework.

## Chạy nhanh

| Việc | Cách làm |
| --- | --- |
| Dựng lại scene graybox 2 tầng + kịch bản dev | Menu `Fire3D/Development/Rebuild Graybox Scene` (thêm `+ Bake Lighting` để bake 2 bộ ánh sáng) |
| Chơi thử | Mở `Scenes/Development/Development_Graybox` → Play. WASD + chuột phải xoay, cuộn chuột zoom, E/F/R hoặc 1/2/3 cho nút hành động, C cúi người, Shift chạy |
| Test | `unity test . --mode EditMode` và `unity test . --mode PlayMode` |
| APK demo (không cần Mobile) | Menu `Fire3D/Build/Demo APK` hoặc `-executeMethod Fire3D.Unity.Editor.AndroidExport.AndroidBuilder.BuildDemoApk` |
| Export cho Mobile (Unity as a Library) | `Fire3D/Build/Export Unity Library for Mobile` → `Builds/AndroidLibrary` |
| Worker build một gói (tay) | `Unity.exe -batchmode -projectPath <project> -executeMethod Fire3D.Unity.Editor.ContentPipeline.BuildWorker.Run -fet3dInput <dir> -fet3dOutput <dir> [-fet3dTarget Android] [-fet3dGpu] [-fet3dNoBake] [-fet3dResult <file>]` |
| Agent worker trên VM (pull từ BE) | [Tools/PackageWorker](Tools/PackageWorker/README.md): `Install-PackageWorker.ps1` cài một lần, `Fire3DPackageWorker.ps1` chạy vòng lặp lease → build → upload → complete |
| Test agent end-to-end trên máy dev | `powershell -File Tools\PackageWorker\Test\Run-LocalTest.ps1 [-Bake -BuildTarget Android]` (export graybox làm input + fake backend) |

**Đầu vào worker:** `building_metadata.json` (`fet3d.building/1`), `scenario.json` (`fet3d.editor/1`) và GLB (mỗi tầng theo `storeys[].chunk`, hoặc một `building.glb`; nếu GLB có một node cấp cao mỗi tầng tên theo `id`/`name`/`ifcGlobalId` của storey thì vẫn tách bundle theo tầng). Tên node = IFC GlobalId, tên material ánh xạ sang `libraryKey` qua `materials`. **Đầu ra:** `floor_<id>` (scene bundle), `floor_<id>_lighting` (bộ ánh sáng mất điện), tài liệu JSON và `manifest.json` (`fet3d.package/1`, đúng validator BE: `jobId`, `buildTarget`, `protocolVersion`, `manifestSchemaVersion`, `runtimeVersion`, `minRuntimeVersion`, `requiredCapabilities`, `floors[].id/bundle/lightingBundle/scene`, `files[]` SHA-256; `PackageManifestFactory.Validate` chặn gói sai trước khi upload).

**Bridge Mobile:** Unity nhận `UnitySendMessage("Fire3DBridge", "Launch", json)` (`LaunchRequest`, protocol 1), có thêm `Pause` và `Abort`. Unity gửi `Ready`, `Launched`, `Events` (lô ≤ 500), `Completed`, `Error`, `Exited` qua `com.fet3d.unity.Fire3DUnityBridge.onUnityMessage(String)`.

## Cấu trúc

```text
Assets/_Fire3D/
├── Runtime/
│   ├── Bootstrap/                    # Khởi tạo và dọn phiên
│   ├── Contracts/                    # Launch, manifest, event, result
│   ├── Session/                      # Training/playtest lifecycle
│   ├── Content/                      # Nạp/gỡ package local
│   ├── Simulation/
│   │   ├── Common/                   # Tick, seed, command/event chung
│   │   ├── Building/                 # Tầng, phòng, cửa, stable IFC ID
│   │   ├── Scenario/                 # Cấu hình và điều kiện kịch bản
│   │   ├── Hazards/                  # Lửa, khói, gió, exposure
│   │   ├── Navigation/               # A*, chi phí và blocked route
│   │   ├── NPC/                      # Trạng thái và di chuyển ECS
│   │   └── Evaluation/               # Mục tiêu, thời gian, rubric
│   ├── Gameplay/
│   │   ├── Player/
│   │   ├── Camera/
│   │   └── Interactions/
│   ├── Presentation/
│   │   ├── SimulationViews/
│   │   ├── UI/
│   │   ├── VFX/
│   │   └── Audio/
│   └── Integration/
│       └── MobileBridge/
├── Authoring/
│   ├── Building/
│   ├── Scenario/
│   ├── Hazards/
│   └── NPC/
├── Editor/
│   ├── Inspectors/
│   ├── ContentPipeline/
│   │   ├── Import/
│   │   ├── Validation/
│   │   └── Build/
│   └── AndroidExport/
├── Scenes/
│   ├── Bootstrap/
│   ├── Training/
│   └── Development/
├── Prefabs/
├── Art/
├── Settings/
└── Tests/
    ├── EditMode/
    ├── PlayMode/
    └── Fixtures/
```

README trong từng folder mô tả trách nhiệm và ví dụ loại file. Namespace gốc `Fire3D.Unity` cho gameplay/presentation/editor; riêng contract và mô phỏng Entities dùng `Fire3D.Contracts` và `Fire3D.Simulation` (xem context về source generator).

Trong mỗi module Simulation, thêm Components/ cho dữ liệu, Systems/ cho điều phối và Jobs/ cho tính toán khi có implementation. Tham khảo [unity-ecs-patterns](.agents/skills/unity-ecs-patterns/SKILL.md): structural changes dùng command buffer; dependency và lifetime native allocation phải rõ; chỉ dùng Burst cho code tương thích, đo hiệu năng trên Android. Ví dụ skill cần được kiểm tra với phiên bản Entities được cài.

## Luồng dữ liệu và quyền sở hữu

1. Mobile gọi backend để prepare/start, tải/verify/cache package và chuyển launch qua bridge.
2. Bootstrap/Session nhận sessionId, manifestPath, grant và protocolVersion theo Docs; Content nạp package local tương thích. Lỗi launch trả về Mobile.
3. Player/interactions gửi command vào simulation tại ranh giới tick. ECS sở hữu hazard/NPC/cửa; player controller sở hữu movement người học và gửi pose/action cho simulation.
4. Presentation đọc snapshot/event sau khi jobs hoàn tất để cập nhật view, HUD, VFX và audio. Không ghi vào native data đang được job sử dụng.
5. Evaluation tạo dữ liệu kết quả; bridge chuyển event/result có sessionId, eventId, schema version và sequence về Mobile. Mobile giữ durable queue và sync API; callback Unity chưa phải xác nhận backend đã lưu.
6. Session kết thúc hoặc launch lỗi phải dọn world/tài nguyên, view binding và event subscription. Session đã start có thể tiếp tục sau mất mạng; package cache không cấp quyền start mới.

Runtime dùng chung giữa các Building; geometry/scenario riêng nằm trong content package. IFC/Blender xử lý upstream; Editor/ContentPipeline nhận artifact đã xử lý, tạo package/manifest và validation output cho backend. Backend sở hữu lease, entitlement, publish và kết quả lưu chính thức.

## Assembly và vị trí asset

Giữ hai asmdef hiện có trong scaffold. Trước khi thêm ECS code, triển khai ranh giới sau cùng dependency tương thích:

| Assembly dự kiến | Nơi đặt | Phụ thuộc nội bộ |
| --- | --- | --- |
| Fire3D.Unity.Contracts | Runtime/Contracts | Không phụ thuộc assembly tính năng |
| Fire3D.Unity.Simulation | Runtime/Simulation | Contracts |
| Fire3D.Unity.Runtime (hiện có) | Runtime | Contracts, Simulation |
| Fire3D.Unity.Authoring | Authoring | Contracts, Simulation |
| Fire3D.Unity.Editor (hiện có, Editor-only) | Editor | Runtime, Contracts, Simulation, Authoring |
| Fire3D.Unity.Tests.EditMode / PlayMode | Tests/EditMode / PlayMode | Assembly cần kiểm thử; cấu hình test-only trước khi thêm test code |

Authoring chứa component cấu hình và baker; không đánh dấu toàn bộ assembly này Editor-only vì component authoring có thể gắn vào asset. API UnityEditor/custom inspector thuộc Editor. Simulation không tham chiếu Presentation, bridge hoặc UnityEditor; Contracts không chứa Entity/World hay kiểu JNI.

Prefab dùng chung ở Prefabs; model/material/texture/audio ở Art, chia theo feature khi có asset. Settings giữ cấu hình không chứa secret. Development scenes và Fixtures dành cho dữ liệu giả, không vào release mặc định. Build/export output ở Builds/ bị ignore; không đưa package tải về, Library, Logs hay UserSettings lên Git.

## Kiểm tra và triển khai tiếp

Scaffold cần kiểm tra đường dẫn, .meta/GUID, liên kết và diff. Không coi folder README hoặc assembly definition là gameplay đã chạy.

Khi triển khai code, nghiệm thu:
- EditMode: manifest/protocol sai bị từ chối; hazard/scoring có kết quả tái lập trong toolchain mục tiêu với seed/tick cố định và tolerance được xác định.
- Navigation: đường bị chặn làm cập nhật route; không có đường thoát được trả thành trạng thái rõ ràng.
- PlayMode: tương tác cập nhật simulation/view; launch lỗi và mở/đóng nhiều phiên không giữ state, subscription hoặc native allocation.
- Mobile/Android: launch/event/result đúng phiên, tiếp tục sau mất mạng khi đã start, sync qua Mobile và chạy đúng sau nhiều lần mở/đóng Unity.
- Benchmark hazard/NPC và bộ nhớ trên Android sau khi có representative content; chưa khẳng định FPS hoặc quy mô NPC từ scaffold.

Đối chiếu Docs technology mục 7–8.1 khi có repo Docs cạnh Unity. Nếu clone riêng thiếu Docs, yêu cầu contract trước khi triển khai bridge/package. Quy trình Git nằm ở [.codex/workflow.md](.codex/workflow.md); hướng dẫn hiện trạng ở [.codex/context.md](.codex/context.md).
