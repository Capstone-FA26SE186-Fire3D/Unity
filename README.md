# Fire3D Unity

Runtime Android cho tập huấn sơ tán FET3D: IFC → 3D → Unity Android → QR → Training → Result.

## Trạng thái

Repo hiện có scaffold folder, README và hai assembly Runtime/Editor. Chưa có gameplay, scene, Android bridge hoặc content pipeline thực thi. Hướng đã chọn: **ECS từ đầu cho hazard/NPC**, GameObject/MonoBehaviour cho player, camera góc nhìn thứ ba, tương tác và UI.

## Môi trường

- Editor theo [ProjectVersion.txt](ProjectSettings/ProjectVersion.txt): Unity 6000.6.3f1. Giữ phiên bản này trong đợt scaffold; Docs đang ghi Unity 6 LTS, cần thống nhất toolchain trước triển khai runtime.
- Android Build Support gồm Android SDK/NDK và OpenJDK khi build Android.
- Git LFS cho binary assets theo .gitattributes.
- [manifest.json](Packages/manifest.json) đã khai báo URP `17.5.0`, tương thích với Unity 6000.6.3f1. Unity Package Manager sẽ bổ sung dependency vào `packages-lock.json` khi mở project. Entities, Burst, Addressables và Test Framework vẫn chưa cài. Tạo URP Pipeline Asset và gán vào Graphics/Quality Settings là bước Editor tiếp theo.

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

README trong từng folder mô tả trách nhiệm và ví dụ loại file. Giữ tên namespace gốc Fire3D.Unity; code đặt theo feature, ví dụ Fire3D.Unity.Simulation.Hazards.

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
