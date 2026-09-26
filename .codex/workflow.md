# Quy trình làm việc — Unity

## Trước khi làm

- Đọc AGENTS, context, workflow, inventory skill và handoff local. Chỉ đọc tài liệu Docs liên quan task.
- Chạy `git status --short --branch`, `git branch -vv`, rồi `git fetch origin --prune`. Nếu remote/base chưa xác minh, dừng tạo branch, đồng bộ, merge, push hoặc release; chỉ làm kiểm tra local và nêu giới hạn.
- Giữ nguyên thay đổi người khác; không stash, reset, clean, xóa generated file hoặc checkout nhánh khác khi chưa an toàn.

## Nhánh và PR

- Khi remote có `develop` đã xác minh, tạo `feature/<task>`, `fix/<task>` hoặc `chore/<task>` từ `origin/develop`.
- PR vào `develop`, ghi phạm vi Unity/Mobile/BE/Docs bị ảnh hưởng, lệnh kiểm tra thực tế và giới hạn. Chỉ PR `develop → main` khi được yêu cầu phát hành.
- Nếu repo chưa có lịch sử hoặc thiếu `develop`, báo rõ và chờ quyết định của người có quyền tạo lịch sử/nhánh; không tự khởi tạo lịch sử hay thay default branch.

## Kiểm tra phù hợp

- Với hướng dẫn/docs: kiểm tra link, diff và ignore.
- Với Unity code: dùng Unity Editor version đúng project để compile/test scene theo phạm vi. Android build hoặc thiết bị thật chỉ chạy khi task yêu cầu và Android support đã sẵn sàng.
- Không coi mở Editor, compile, play mode hay build APK là bằng chứng package integrity, authorization, QR/start gate, native bridge hoặc offline-sync production.
- Không đưa API key, signed URL, package private hay dữ liệu production vào scene, prefab, ScriptableObject hoặc log.

## Ghi chú

- `.codex/local/` là local-only. Cập nhật handoff sau mốc đáng kể với branch/commit quan sát được, thay đổi, kiểm tra, blocker và bước tiếp.
- `.codex/lessons.md` chỉ chứa bài học ổn định có bằng chứng, không phải nhật ký phiên.

