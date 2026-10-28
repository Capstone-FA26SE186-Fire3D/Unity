# Context — Unity

## Contract sản phẩm hiện hành — Docs v7

Nguồn chuẩn: [v7 contract](../../Docs/schema_v7_contract.md) và [requirements](../../Docs/fire_evacuation_requirements.md). Đây là thiết kế đích, không phải bằng chứng runtime đã triển khai.

- Organization tự soạn hoặc dùng template tùy chọn trong Organization Library; PlatformAdmin duyệt từng scenario/rubric version trước publish. Submit đóng băng hash; sửa tạo version và review mới. IFC QA/ConfirmForTraining là readiness kỹ thuật, không thay approval nội dung.
- Library do Admin quản lý, tách khỏi Learn public. Metadata thiết bị chỉ mô tả capability runtime đã hỗ trợ; thêm metadata không tự tạo gameplay mới. Cập nhật mẫu không ghi đè package/rubric đã pin.
- Trainee tự chọn Learn, Guided Drill hoặc Assessment, không cần học/luyện trước, retry không giới hạn; không curriculum/sprint hoặc certificate. Learn blog web khác mode Learn Unity.
- Unity gửi metrics/events; backend quyết định score, criterion results, rubric hash, outcome `Passed|NotPassed|Incomplete|NotAssessed` và lý do. Session Completed không tự động Passed; không tự đặt ngưỡng đạt.
- Building Private mặc định; participation code tạo grant theo account và `access_revision`. Đổi/thu hồi code hoặc đổi visibility làm grant cũ mất hiệu lực. Public vẫn yêu cầu Trainee đăng nhập; QR chỉ resolve Building.
- List/package/prepare kiểm quyền; online start recheck access, Approved + Published scenario, entitlement và learner seat. Session đã start pin entitlement/review/rubric, tiếp tục/sync được sau expiry/revoke; không mở session mới offline.
- Gói Building 6/12 tháng có hạn mức Trainee khác nhau/kỳ. Backend tính một suất khi user bắt đầu lần đầu tại Building/kỳ; replay/bài khác không thêm suất, playtest không tính. Unity không quyết định seat, payment hoặc quota; Organization dùng AI trả trước pooled, Trainee quota ngày riêng.
- AI bị chặn trong Assessment. Learner RAG chỉ dùng snapshot scenario `name`, `objectives`, `instructions` đã approved/published và hiện còn quyền; không gửi rubric, đáp án, draft hoặc IFC/BIM private cho AI. Sau mất quyền chỉ còn giải thích kết quả cá nhân đã lưu và Common, không retrieval Building mới.

## Hiện trạng quan sát

- Đã có runtime trên `main`, phiên bản tích hợp Mobile `2.0.2`: contract `fet3d.editor/1`/`fet3d.building/1`/launch/event/manifest, mô phỏng Entities (lửa/khói/nhiệt, cửa, cầu thang, sprinkler, đầu báo, timeline, NPC A*), camera third-person, tương tác theo ngữ cảnh, HUD, phiên tập huấn + telemetry, bridge Mobile, nạp gói AssetBundle, scene graybox 2 tầng, worker bake. Các kết quả test từng phiên nằm trong handoff/PR; task cập nhật context không chạy lại Unity tests.
- Agent worker pull-mode (17/10/2026, `Tools/PackageWorker`, Windows PowerShell 5.1): lease → tải input → `Unity.exe -batchmode BuildWorker.Run -fet3dResult` → renew → upload-urls/PUT với header checksum → outputs ×2 → complete; khóa `X-Worker-Key` DPAPI; Scheduled Task. Manifest `fet3d.package/1` khớp validator BE (`PackageManifestFactory`). Đã PASS end-to-end với fake backend (`Test/Run-LocalTest.ps1`). VM `UNITY-WORKER` (Azure Malaysia West, D4s v4, Windows Server 2022, Unity 6000.6.6f1 + Android) đã clone `C:\fet3d\unity` và cài agent; Scheduled Task mặc định chạy lúc VM boot (logon Password, nhập mật khẩu Windows một lần; không dùng S4U vì không giải mã được DPAPI/Git credentials). Chạy tay Unity trên VM phải chờ tiến trình (`Start-Process -Wait`), vì `& Unity.exe` trả về ngay. Lỗi lease 500 (BE#118) là quan sát lịch sử ngày 17/10; không dùng làm trạng thái blocker hiện tại nếu chưa tái kiểm.
- Mobile đã có native bridge và APK tích hợp runtime; Unity đã có model/animation Tripo–Mixamo (chi tiết bên dưới). Nghiệm thu trọn luồng GLB worker thật → package → điện thoại, native lifecycle/offline sync và benchmark vẫn cần bằng chứng riêng.
- README yêu cầu Unity `6000.6.6f1`, Android Build Support gồm SDK/NDK/OpenJDK và Git LFS (jpg/png/glb đã vào LFS).

## Vai trò trong FET3D

- FET3D tập huấn sơ tán theo luồng `IFC → 3D → Unity Android → QR → Training → Result`.
- Unity là gameplay runtime. Mobile React Native/Expo nhận quyền/session/package qua .NET API rồi mở Unity bằng native Android bridge; Three.js chỉ dành cho landing/editor/preview web.
- Hướng trải nghiệm là BIM 3D với camera góc nhìn thứ ba (third-person) bám theo nhân vật, orbit/zoom có giới hạn và xử lý va chạm/che khuất theo [Docs technology mục 1.2](../../Docs/fire-evacuation-training-technology.md#12-gameplay-third-person-và-biểu-diễn-bim). Giữ cấu trúc, kích thước và định danh IFC; không di chuyển tường để camera vừa. Mô phỏng phục vụ tập huấn, không phải chứng nhận PCCC hay chỉ dẫn khẩn cấp thực tế.
- Unity không trực tiếp gọi PostgreSQL, Redis, S3 bằng credential hay AI service. Backend xác minh identity, Building access, entitlement, learner seat, scenario approval/publish và package/runtime compatibility rồi cấp launch grant sau explicit online start; QR không cấp quyền.
- Mobile sở hữu tải/verify/cache artifact qua signed URL, gọi API và durable queue sync. Unity nhận manifestPath/grant qua bridge, kiểm tra khả năng tiếp nhận và nạp package local, trả event/result/lỗi. Session đã start có thể tiếp tục offline và Mobile sync idempotent sau đó; session mới không được tự start offline.
- Worker IFC/Blender và Unity Editor build chạy ngoài client, claim/lease qua backend, đăng ký artifact/validation provenance rồi backend mới publish. Worker không tự ghi billing, entitlement hoặc publish.

## Lựa chọn kỹ thuật

Chốt 16–17/10/2026 (trưởng nhóm):

- Editor **6000.6.6f1** (nâng từ 6000.6.3f1 ngày 17/10/2026 vì có bản vá UaaL relaunch trên Android, light probe nhiều scene, AssetBundle tham chiếu chéo), URP 17.6, Entities 6.6, Input System, uGUI; không dùng Addressables (nạp AssetBundle trực tiếp).
- **Entities ngay từ đầu** cho lưới lửa/khói/nhiệt và NPC (`Fire3D.Simulation`). Entity tạo lúc runtime từ metadata + kịch bản, không cần baking/SubScene. NPC: ECS giữ trạng thái/A*, hiển thị bằng GameObject pool (sau này Mixamo). Player, camera, tương tác, UI, bridge dùng MonoBehaviour.
- **Ưu tiên hình ảnh:** Unity build worker chạy batch trên server của team (Azure VM Windows) — import GLB, gán material theo `libraryKey`, collider, bake lightmap + light probe + reflection probe cho hai bộ ánh sáng (bình thường, mất điện), AssetBundle theo tầng + manifest SHA-256. Hậu kỳ URP: ACES, bloom, vignette.
- Texture PBR chỉ nằm trong `Art/MaterialLibrary` và được đóng thành bundle `fet3d-materials` cài sẵn trong app; GLB chỉ mang `libraryKey`.
- Namespace của assembly có code Entities sinh tự động **không được chứa đoạn `Fire3D.Unity`** (`Fire3D.Simulation`, `Fire3D.Contracts`), vì code sinh ra gọi `Unity.Entities…` sẽ bị hiểu thành `Fire3D.Unity.Entities`.
- Unity chỉ ghi event (GUID `eventId`, `schemaVersion` dạng chuỗi, `Moved`/`HazardExposure` mỗi 5 giây, event capability theo `runtime-catalog.batch1`); backend chấm Passed/NotPassed. Thông số an toàn (khăn, sprinkler) chỉ lấy từ tham số kịch bản đã duyệt, không có giá trị mặc định.
- **HUD mobile (18/10/2026):** bố cục kiểu PUBG/CoD Mobile ngang — joystick nổi vùng ngón cái trái, cụm nút tròn vùng ngón cái phải (nút chính + 2 phụ + cúi), máu giữa dưới, đồng hồ/mục tiêu trên, nhãn nổi trên vật thể, viền cảnh báo nhiệt/khói. Font Be Vietnam Pro (OFL, cùng Mobile) trong `Resources/UI/Fonts`; icon Lucide (ISC) sinh bằng `node Tools/UiIcons/build-icons.mjs ../FE/node_modules` vào `Resources/UI/Icons`. Mỗi nút có icon + chữ, phản hồi nhún/rung (Android VibrationEffect).
- **Ô tương tác (27/10/2026):** hành động với vật (`ActionSource.Target`) là ô nhỏ kiểu PUBG cạnh vật (`HudButton.CreateChip`, tối đa 3, xếp dọc, tên vật ở trên), chạm một lần gọi `InteractionController.Tap`. Hành động giữ (nhúng khăn) tự chạy vòng tiến độ; đi xa > 0.6 m hoặc đổi mục tiêu trước khi xong thì thất bại (`FailTap`), phải làm lại. Nút tròn chỉ còn cho vật đang cầm (`ActionSource.HeldItem`: phun vẫn giữ nút, che mũi). AutoPilot dùng `Tap` cho hành động với vật.
- **Dụng cụ, âm thanh, trời (27/10/2026):** `Fire3D/Content/Build Equipment Prefabs` (`EquipmentPrefabBuilder`) dựng `Prefabs/Equipment/*` từ model free (Poly Haven, Kenney, khăn Blender `Tools/TowelModel`), gán vào `RuntimeAssets.Interactables`, `Sounds` và `Sky`; chạy lại sau khi đổi model/âm thanh. Nguồn/giấy phép ở `Art/Equipment/CREDITS.md`, `Art/Audio/CREDITS.md`; âm tự tổng hợp bằng `Tools/AudioSynth/generate.py`. Âm phát qua `GameAudio` (3D tạm thời / loop / UI) và `PlayerAudio` (bước chân, ho, thở). Khăn có hai mesh gấp/rủ, `TowelView` đổi theo `Held`, bám xương tay phải và sẫm khi ướt. Thư viện vật liệu có 21 khóa (thêm epoxy, vinyl, terrazzo, rubber, gạch trắng, sơn, inox).
- **Cửa và cử chỉ:** sờ/mở/đóng cửa là hành động có thân thể (quay mặt → đưa tay → tác dụng lúc chạm → hạ tay, `InteractionController.Busy`), cánh mở ra xa người mở, collider cánh luôn bật. Phần chuyển động (cánh cửa) không được BatchingStatic/OccluderStatic khi bake (`LightingBaker.PrepareStatic`), nếu không hình cánh đứng yên dù collider đã xoay. NPC hiển thị bằng CharacterController đi theo vị trí ECS nên không xuyên tường/đồ vật.
- **Khói (18/10/2026):** `VolumeSmoke` là một Particle System duy nhất sinh hạt vào ô có khói của `HazardGrid` (theo √nồng độ, trôi theo gradient, giới hạn trong lớp khói, kích thước ≤ 2× khoảng cách tới tường, chết khi lọt vào tường/ô sạch) nên khói lấp phòng, tràn qua cửa mở, rò qua khe cửa đóng và không bao giờ xuyên tường. Lớp khói đọng dưới trần (`SmokeLayer`, trần 3 m, dày tối đa 2,6 m) hạ dần theo thời gian, màu trắng→đen theo `SmokeAge` (~60 s). Người chơi có 3 tư thế đứng/cúi/bò (nút cúi xoay vòng, `PlayerMotor.CycleStance`), hít khói theo đầu so với đáy lớp; chế độ hướng dẫn nhắc cúi/bò. Shader `Fire3D/SmokeSixWay` sáu hướng, `ZTest Always` + mờ theo độ che để không bị cắt phẳng ở tường.
- **Lửa và ánh sáng (18/10/2026):** lửa/khói là flipbook sinh bằng `python Tools/VfxTextures/generate.py` (8×8, 64 khung, lặp liền) trên Particle System nhiều lớp (thân lửa, lõi, tàn lửa, vệt sáng sàn, hào quang) có frame blending và soft particles (URP asset bật depth texture). Heat haze khúc xạ thật (`Art/VFX/HeatHaze.shader`) chỉ bật ở `FireQuality` Medium/High và chỉ xin opaque texture khi có emitter haze; Low giữ bản rẻ. Đèn trần là Rectangle area light (chỉ bake), ambient riêng cho kịch bản bình thường/mất điện, bake 4 bounce + OpenImage denoiser + AO rộng hơn, hậu kỳ bloom mềm + split toning. `BuildWorker` tạo UV2 và tangent cho mesh GLB thiếu UV2 (Blender thường không xuất UV2).
- **Nhân vật (19/10/2026):** model làm trên Tripo Studio, làm sạch trong Blender (tên, chiều cao thật, gốc ở chân, texture JPEG 2048), **rig bằng Mixamo** (Standard Skeleton 65) rồi đặt vào `Art/Characters/<Tên>/<Tên>.fbx`; clip Mixamo dùng chung trong `Art/Characters/Animations` (Humanoid, lặp + tại chỗ). `CharacterPrefabBuilder.Build(true)` tách texture, dựng `Humanoid.controller` (Idle/Walking/Jogging theo Speed × StrideMul, Crouch, Crawl, Down) và prefab `Prefabs/Characters`, gán `RuntimeAssets.PlayerVariants/NpcVariants`. `CharacterAnimator` nhận Speed/Stance/Down từ `PlayerMotor`/`NpcPresenter` và đặt tay phải bằng IK khi chạm/mở cửa. Hiện có PlayerMale, PlayerFemale, NpcWorker; 7 NPC còn lại chờ rig, NPC thiếu mẫu vẫn dùng viên nang. Rig Tripo bị loại vì retarget làm chân cong ra sau, mũi chân hếch.
- **Kiểm tra bằng bot:** PlayMode `AutoPilotTests` (bot chơi trọn kịch bản, report + ảnh trong `Builds/Autoplay`), `WallContactTests` (không lún tường, không dính tường). Khi Editor của người dùng đang mở project thì batchmode bị khóa: điều khiển Editor bằng `unity command` (recompile, run_tests --async_tests, eval). Chỉ Android là nền tảng chơi; không build desktop; emulator x86_64 không chạy được Unity 6000.6.
- API contract, auth, launch grant và package contract thuộc .NET/Docs; Unity bọc chúng bằng adapter rõ ràng, không nhân bản business authorization tại client. Không hard-code endpoint, key hoặc thông tin cá nhân.

## Tài liệu cần đối chiếu

- [Docs README](../../Docs/README.md)
- [Project overview](../../Docs/fire_evacuation_project_overview.md)
- [Technology architecture](../../Docs/fire-evacuation-training-technology.md)
- [Mobile context](../../Mobile/.codex/context.md) khi sửa bridge/session
- [AI context](../../AI/.codex/context.md) khi sửa IFC/worker contract

Thiết kế Docs có thể đi trước implementation. Không tuyên bố pipeline IFC/Unity, Android bridge, package validation, session offline sync hay ECS đã hoàn thành nếu chưa có kiểm thử tương ứng.

## IFC ingestion, hiển thị và tương tác — 2026-10-15

Nguồn: [IFC ingestion, hiển thị và tương tác](../../Docs/ifc_ingestion_visual_interaction.md) (Docs PR #15/#16). Tài liệu gồm thiết kế và bằng chứng spike cục bộ trên 32 file IFC công khai. Source hiện đã có IFC worker (`AI/ifc-worker`), API/gate xử lý ở BE và Unity build worker; nghiệm thu từng bước với IFC thật, QA và Android vẫn phải kiểm tra riêng. Mục 4 (nhiều IFC cho một revision), ngân sách tam giác và preset vật liệu còn chờ team chốt.

Quy tắc chung đã chốt với nhóm: chỉ nhận IFC (RVT tự xuất IFC ở ngoài); **IFC có thiết bị PCCC/nguồn nước thì pipeline tự tạo, không có thì OrganizationUser đặt trong editor**; mô hình 3D thiết bị luôn lấy từ thư viện dùng chung (có thể tạo bằng công cụ AI như Tripo rồi dọn lưới), không lấy từ IFC; IFC hầu như không mang màu/texture dùng được nên vật liệu lấy từ thư viện PBR CC0 theo preset.

Phụ trách: Phạm Gia Bảo (Unity). Việc liên quan repo này:

- Runtime: `Interactable`, `InteractionDetector`, `ActionBar` theo luật tương tác ngữ cảnh ở mục 7 (gần vật + vật đang cầm + trạng thái, tối đa 3 ô chạm cạnh vật, thao tác có thời gian tự chạy và thất bại khi rời đi, bẫy vẫn hiện ô, Guided khác Assessment); HUD người chơi và biểu tượng trạng thái NPC ở mục 8.
- Catalog đợt 1 (mục 6.1) và sprinkler tự động (mục 6.3); hiệu ứng là tham số từ `ScenarioVersion`, không hard-code tác dụng an toàn. Hazard/sprinkler thuộc ECS, tương tác thuộc MonoBehaviour như ranh giới hiện có.
- Đọc `building_metadata.json` (mục 5.7) để gắn component theo `catalogKey` và `ifcGlobalId`.
- Unity build worker (`Editor/ContentPipeline`): import GLB có UV0 box theo mét và UV1 lightmap, URP Lit từ thư viện PBR, bake lightmap/light probe/reflection probe, preset ánh sáng ban ngày/mất điện/khói (mục 5.6), package theo tầng, ngân sách khởi điểm ≤150k tam giác hiển thị phải đo lại trên Android tầm trung.
