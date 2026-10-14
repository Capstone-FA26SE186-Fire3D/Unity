# Fire3D Unity — hướng dẫn Codex

- Khi bắt đầu task, đọc `.codex/context.md`, `.codex/workflow.md`, `.codex/skills-inventory.md` và `.codex/local/handoff.md` nếu có. Nếu thiếu local notes, tạo riêng phần thiếu theo `.codex/bootstrap.md`; không ghi đè.
- Trước khi sửa Unity, đối chiếu [Docs/README.md](../Docs/README.md) và tài liệu đúng phạm vi. Docs là nguồn quyết định sản phẩm; code/prototype không là bằng chứng production.
- Unity là runtime Android cho gameplay mô phỏng; React Native/Expo Mobile mở Unity qua native Android bridge. Không thay gameplay bằng Three.js, không đặt credential, token dài hạn, service-role key hay quyền nghiệp vụ trong Unity.
- Hướng UX là BIM 3D với camera góc nhìn thứ ba bám theo nhân vật, orbit/zoom có giới hạn và xử lý va chạm/che khuất. IFC/Blender và Unity Editor build là worker pipeline tách biệt; Unity client chỉ nhận package đã được backend cấp quyền và xác thực.
- Đã chọn ECS từ đầu cho hazard/NPC; dùng [unity-ecs-patterns](.agents/skills/unity-ecs-patterns/SKILL.md) khi triển khai mô phỏng/Jobs/Burst. Player, camera, UI và bridge dùng GameObject/MonoBehaviour. Xem README cho cấu trúc feature và ranh giới assembly; scaffold chưa cài Entities/URP/Addressables.
- Không commit `Library/`, `Temp/`, `Logs/`, `UserSettings/`, build artifacts hoặc secrets. Tôn trọng `.gitignore`; kiểm tra thay đổi generated trước khi stage.
- Quy trình Git: fetch remote, làm nhánh task từ `origin/develop`, PR vào `develop`, kiểm tra tích hợp rồi chỉ release `main` khi được yêu cầu. Repo chưa có history/remote base xác minh thì không tự tạo branch, commit, push hay phát hành.
- Sau mốc đáng kể, cập nhật `.codex/local/handoff.md`; chỉ nâng bài học ổn định vào `.codex/lessons.md`. Không ghi local notes khi ở Plan Mode.
