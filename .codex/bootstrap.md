# Khởi tạo ghi chú Codex cho Unity

Nếu clone/pull thiếu bộ ghi chú, chỉ tạo file còn thiếu: `AGENTS.md`, `.codex/context.md`, `.codex/workflow.md`, `.codex/skills-inventory.md`, `.codex/lessons.md`, `.codex/.gitignore` và local notes. Không ghi đè file đã có, không init Git, không tạo history/branch, không copy secrets hay local notes của người khác.

Trước khi tạo local notes, xác minh `.codex/local/` bị ignore và không có local note đã track. Local handoff ghi repo, thời điểm, branch/HEAD quan sát được, task, kiểm tra và bước tiếp; local lessons khởi đầu bằng trạng thái chưa có bài học. Bootstrap không cấp quyền commit, push, PR, merge hoặc chạy build.

Nếu mở từ workspace Fire3D, đọc Docs/README và root `.codex`; không ghi vào Docs. Nếu thiếu Docs hoặc remote base, nêu rõ giới hạn thay vì đoán contract.

