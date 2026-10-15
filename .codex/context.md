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

- Repo chứa scaffold `Assets/_Fire3D/` gồm Runtime, Authoring, Editor, Scenes, Prefabs, Art, Settings và Tests; xem [README](../README.md) cho sơ đồ và trách nhiệm. Chưa có gameplay/scene/bridge thực thi.
- README yêu cầu Unity `6000.6.3f1` (Unity 6.6), Android Build Support gồm SDK/NDK/OpenJDK và Git LFS cho binary assets.
- Repo đã có lịch sử Git, origin là `Capstone-FA26SE186-Fire3D/Unity`. Branch/commit và kiểm tra từng phiên nằm trong local handoff; xác minh remote trước task mới.

## Vai trò trong FET3D

- FET3D tập huấn sơ tán theo luồng `IFC → 3D → Unity Android → QR → Training → Result`.
- Unity là gameplay runtime. Mobile React Native/Expo nhận quyền/session/package qua .NET API rồi mở Unity bằng native Android bridge; Three.js chỉ dành cho landing/editor/preview web.
- Hướng trải nghiệm là BIM 3D, camera cố định 2.5D. Mô phỏng phục vụ tập huấn, không phải chứng nhận PCCC hay chỉ dẫn khẩn cấp thực tế.
- Unity không trực tiếp gọi PostgreSQL, Redis, S3 bằng credential hay AI service. Backend xác minh identity, Building access, entitlement, learner seat, scenario approval/publish và package/runtime compatibility rồi cấp launch grant sau explicit online start; QR không cấp quyền.
- Mobile sở hữu tải/verify/cache artifact qua signed URL, gọi API và durable queue sync. Unity nhận manifestPath/grant qua bridge, kiểm tra khả năng tiếp nhận và nạp package local, trả event/result/lỗi. Session đã start có thể tiếp tục offline và Mobile sync idempotent sau đó; session mới không được tự start offline.
- Worker IFC/Blender và Unity Editor build chạy ngoài client, claim/lease qua backend, đăng ký artifact/validation provenance rồi backend mới publish. Worker không tự ghi billing, entitlement hoặc publish.

## Lựa chọn kỹ thuật

- Editor hiện là 6000.6.3f1; Docs đang ghi Unity 6 LTS. Manifest đã khai báo URP 17.5.0; mở project bằng Unity Editor sẽ resolve và ghi dependency vào packages-lock.json, sau đó cần tạo/gán URP Pipeline Asset trong Graphics/Quality Settings. Addressables, Entities, Burst và Test Framework chưa cài. Scaffold giữ nguyên Packages/ProjectSettings.
- Người dùng đã chọn ECS từ đầu cho hazard/NPC, dùng Jobs/Burst khi tương thích. GameObject/MonoBehaviour giữ player, camera 2.5D, interactions, UI và bridge. ECS sở hữu trạng thái hazard/NPC/cửa; gameplay gửi command tại tick, presentation đọc snapshot sau jobs. Đo hiệu năng và bộ nhớ Android khi có representative content.
- Authoring chứa cấu hình và baker; Editor chứa inspector và content pipeline. Hiện chỉ giữ hai assembly Runtime/Editor; ranh giới assembly dự kiến và acceptance scenarios ở README. Chưa thêm Components/Systems/Jobs rỗng hoặc class giả.
- API contract, auth, launch grant, telemetry/event sequence và package contract thuộc .NET/Docs; Unity bọc chúng bằng adapter rõ ràng, không nhân bản business authorization tại client.
- Không hard-code endpoint production, API key hoặc thông tin cá nhân. Cấu hình theo environment, secret injection/build pipeline đã được duyệt.

## Tài liệu cần đối chiếu

- [Docs README](../../Docs/README.md)
- [Project overview](../../Docs/fire_evacuation_project_overview.md)
- [Technology architecture](../../Docs/fire-evacuation-training-technology.md)
- [Mobile context](../../Mobile/.codex/context.md) khi sửa bridge/session
- [AI context](../../AI/.codex/context.md) khi sửa IFC/worker contract

Thiết kế Docs có thể đi trước implementation. Không tuyên bố pipeline IFC/Unity, Android bridge, package validation, session offline sync hay ECS đã hoàn thành nếu chưa có kiểm thử tương ứng.

## IFC ingestion, hiển thị và tương tác — 2026-10-15

Nguồn: [IFC ingestion, hiển thị và tương tác](../../Docs/ifc_ingestion_visual_interaction.md) (Docs PR #15/#16). Đây là **đề xuất thiết kế** có bằng chứng spike cục bộ trên 32 file IFC công khai; chưa có worker, API, migration hay code Unity tương ứng. Mục 4 (nhiều IFC cho một revision), ngân sách tam giác và preset vật liệu còn chờ team chốt.

Quy tắc chung đã chốt với nhóm: chỉ nhận IFC (RVT tự xuất IFC ở ngoài); **IFC có thiết bị PCCC/nguồn nước thì pipeline tự tạo, không có thì OrganizationUser đặt trong editor**; mô hình 3D thiết bị luôn lấy từ thư viện dùng chung (có thể tạo bằng công cụ AI như Tripo rồi dọn lưới), không lấy từ IFC; IFC hầu như không mang màu/texture dùng được nên vật liệu lấy từ thư viện PBR CC0 theo preset.

Phụ trách: Phạm Gia Bảo (Unity). Việc liên quan repo này:

- Runtime: `Interactable`, `InteractionDetector`, `ActionBar` theo luật tương tác ngữ cảnh ở mục 7 (gần vật + vật đang cầm + trạng thái, tối đa 3 nút, nút giữ, bẫy vẫn hiện nút, Guided khác Assessment); HUD người chơi và biểu tượng trạng thái NPC ở mục 8.
- Catalog đợt 1 (mục 6.1) và sprinkler tự động (mục 6.3); hiệu ứng là tham số từ `ScenarioVersion`, không hard-code tác dụng an toàn. Hazard/sprinkler thuộc ECS, tương tác thuộc MonoBehaviour như ranh giới hiện có.
- Đọc `building_metadata.json` (mục 5.7) để gắn component theo `catalogKey` và `ifcGlobalId`.
- Unity build worker (`Editor/ContentPipeline`): import GLB có UV0 box theo mét và UV1 lightmap, URP Lit từ thư viện PBR, bake lightmap/light probe/reflection probe, preset ánh sáng ban ngày/mất điện/khói (mục 5.6), package theo tầng, ngân sách khởi điểm ≤150k tam giác hiển thị phải đo lại trên Android tầm trung.
