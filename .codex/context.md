# Context — Unity

## Hiện trạng quan sát

- Repo chứa Unity project Fire3D với layout `Assets/_Fire3D/{Runtime,Editor,Scenes,Art}`, `Packages/` và `ProjectSettings/`.
- README yêu cầu Unity `6000.6.3f1` (Unity 6.6), Android Build Support gồm SDK/NDK/OpenJDK và Git LFS cho binary assets.
- Lần khởi tạo ghi chú này, checkout local chưa có commit và mọi file Unity ban đầu đang untracked. Origin là `Capstone-FA26SE186-Fire3D/Unity`, nhưng chưa xác minh được nhánh base/remote refs; không coi trạng thái local là mới nhất hay tạo lịch sử Git thay team.

## Vai trò trong FET3D

- FET3D tập huấn sơ tán theo luồng `IFC → 3D → Unity Android → QR → Training → Result`.
- Unity là gameplay runtime. Mobile React Native/Expo nhận quyền/session/package qua .NET API rồi mở Unity bằng native Android bridge; Three.js chỉ dành cho landing/editor/preview web.
- Hướng trải nghiệm là BIM 3D, camera cố định 2.5D. Mô phỏng phục vụ tập huấn, không phải chứng nhận PCCC hay chỉ dẫn khẩn cấp thực tế.
- Unity không trực tiếp gọi PostgreSQL, Redis, S3 bằng credential hay AI service. Backend xác minh identity, Building entitlement, QR, package/runtime compatibility và cấp launch grant sau explicit online start.
- Client chỉ tải artifact/package qua signed URL ngắn hạn, kiểm tra package hash, manifest hash, build target, protocol/schema và runtime version trước khi launch. Session đã start có thể tiếp tục offline và sync event/result idempotent sau đó; session mới không được tự start offline.
- Worker IFC/Blender và Unity Editor build chạy ngoài client, claim/lease qua backend, đăng ký artifact/validation provenance rồi backend mới publish. Worker không tự ghi billing, entitlement hoặc publish.

## Lựa chọn kỹ thuật

- Ưu tiên Unity 6.6 + URP theo project hiện có. Android target là đầu tiên; addressables, NavMesh, collider, asset import và build automation chỉ thêm khi task/benchmark xác nhận.
- C# runtime bắt đầu đơn giản bằng component/service phù hợp Unity. ECS/DOTS, Job System và Burst chỉ phù hợp khi profiling có tải entity/CPU rõ ràng; đổi kiến trúc phải có benchmark, fallback và quyết định đã review.
- API contract, auth, launch grant, telemetry/event sequence và package contract thuộc .NET/Docs; Unity bọc chúng bằng adapter rõ ràng, không nhân bản business authorization tại client.
- Không hard-code endpoint production, API key hoặc thông tin cá nhân. Cấu hình theo environment, secret injection/build pipeline đã được duyệt.

## Tài liệu cần đối chiếu

- [Docs README](../../Docs/README.md)
- [Project overview](../../Docs/fire_evacuation_project_overview.md)
- [Technology architecture](../../Docs/fire-evacuation-training-technology.md)
- [Mobile context](../../Mobile/.codex/context.md) khi sửa bridge/session
- [AI context](../../AI/.codex/context.md) khi sửa IFC/worker contract

Thiết kế Docs có thể đi trước implementation. Không tuyên bố pipeline IFC/Unity, Android bridge, package validation, session offline sync hay ECS đã hoàn thành nếu chưa có kiểm thử tương ứng.

