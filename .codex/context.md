# Context — Unity

## Hiện trạng quan sát

- Repo chứa scaffold `Assets/_Fire3D/` gồm Runtime, Authoring, Editor, Scenes, Prefabs, Art, Settings và Tests; xem [README](../README.md) cho sơ đồ và trách nhiệm. Chưa có gameplay/scene/bridge thực thi.
- README yêu cầu Unity `6000.6.3f1` (Unity 6.6), Android Build Support gồm SDK/NDK/OpenJDK và Git LFS cho binary assets.
- Repo đã có lịch sử Git, origin là `Capstone-FA26SE186-Fire3D/Unity`. Branch/commit và kiểm tra từng phiên nằm trong local handoff; xác minh remote trước task mới.

## Vai trò trong FET3D

- FET3D tập huấn sơ tán theo luồng `IFC → 3D → Unity Android → QR → Training → Result`.
- Unity là gameplay runtime. Mobile React Native/Expo nhận quyền/session/package qua .NET API rồi mở Unity bằng native Android bridge; Three.js chỉ dành cho landing/editor/preview web.
- Hướng trải nghiệm là BIM 3D, camera cố định 2.5D. Mô phỏng phục vụ tập huấn, không phải chứng nhận PCCC hay chỉ dẫn khẩn cấp thực tế.
- Unity không trực tiếp gọi PostgreSQL, Redis, S3 bằng credential hay AI service. Backend xác minh identity, Building entitlement, QR, package/runtime compatibility và cấp launch grant sau explicit online start.
- Mobile sở hữu tải/verify/cache artifact qua signed URL, gọi API và durable queue sync. Unity nhận manifestPath/grant qua bridge, kiểm tra khả năng tiếp nhận và nạp package local, trả event/result/lỗi. Session đã start có thể tiếp tục offline và Mobile sync idempotent sau đó; session mới không được tự start offline.
- Worker IFC/Blender và Unity Editor build chạy ngoài client, claim/lease qua backend, đăng ký artifact/validation provenance rồi backend mới publish. Worker không tự ghi billing, entitlement hoặc publish.

## Lựa chọn kỹ thuật

- Editor hiện là 6000.6.3f1; Docs đang ghi Unity 6 LTS, cần thống nhất toolchain khi triển khai dependencies. URP + Addressables là hướng thiết kế; manifest chưa có URP, Addressables, Entities, Burst hoặc Test Framework. Scaffold giữ nguyên Packages/ProjectSettings.
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
