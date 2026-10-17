# Unity — bài học dùng chung

Chỉ lưu kiến thức đã xác minh và có ích dài hạn cho team. Nhật ký phiên nằm trong `.codex/local/`.

Khi thêm trong PR liên quan, ghi ngắn: tình huống → bằng chứng → cách xử lý → kiểm tra và phạm vi áp dụng.

- **Cánh cửa bake bị static batching (18/10/2026):** mở cửa thì trạng thái/collider đổi nhưng hình cánh đứng yên; người chơi trông như đi xuyên cửa. Nguyên nhân: `PrepareStatic` gắn `BatchingStatic` cho mọi MeshRenderer, kể cả cánh cửa. Sửa: phần dưới `DoorView.leaf` chỉ `ContributeGI | ReflectionProbeStatic`. Kiểm tra: PlayMode `OpenedDoorLeafVisiblySwingsAwayFromPlayer` (leaf không `isPartOfStaticBatch`, tâm mesh dịch > 0.25 m). Áp dụng cho mọi phần chuyển động trong package.
- **Khe lan can hẹp hơn capsule (18/10/2026):** bot tự chơi không xuống được cầu thang từ L2 vì khe giữa lan can và tường 0.5 m < 0.56 m đường kính nhân vật; các test teleport không phát hiện. Cần kiểm tra đường đi bằng bot đi bộ thật cho mọi lối thoát.

