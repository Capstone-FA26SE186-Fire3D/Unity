# Fire3D Unity — hướng dẫn Codex

- Khi bắt đầu task, đọc `.codex/context.md`, `.codex/workflow.md`, `.codex/skills-inventory.md` và `.codex/local/handoff.md` nếu có. Nếu thiếu local notes, tạo riêng phần thiếu theo `.codex/bootstrap.md`; không ghi đè.
- Trước khi sửa Unity, đối chiếu [Docs/README.md](../Docs/README.md) và tài liệu đúng phạm vi. Docs là nguồn quyết định sản phẩm; code/prototype không là bằng chứng production.
- Unity là runtime Android cho gameplay mô phỏng; React Native/Expo Mobile mở Unity qua native Android bridge. Không thay gameplay bằng Three.js, không đặt credential, token dài hạn, service-role key hay quyền nghiệp vụ trong Unity.
- Hướng UX là BIM 3D với camera cố định 2.5D. IFC/Blender và Unity Editor build là worker pipeline tách biệt; Unity client chỉ nhận package đã được backend cấp quyền và xác thực.
- `unity-ecs-patterns` là skill project-local. Chỉ đọc khi task thực sự dùng DOTS/ECS/Jobs/Burst hoặc profiling hiệu năng; không thêm Entities/DOTS hay chuyển MonoBehaviour sang ECS theo mặc định.
- Không commit `Library/`, `Temp/`, `Logs/`, `UserSettings/`, build artifacts hoặc secrets. Tôn trọng `.gitignore`; kiểm tra thay đổi generated trước khi stage.
- Quy trình Git: fetch remote, làm nhánh task từ `origin/develop`, PR vào `develop`, kiểm tra tích hợp rồi chỉ release `main` khi được yêu cầu. Repo chưa có history/remote base xác minh thì không tự tạo branch, commit, push hay phát hành.
- Sau mốc đáng kể, cập nhật `.codex/local/handoff.md`; chỉ nâng bài học ổn định vào `.codex/lessons.md`. Không ghi local notes khi ở Plan Mode.

