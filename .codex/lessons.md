# Unity — bài học dùng chung

Chỉ lưu kiến thức đã xác minh và có ích dài hạn cho team. Nhật ký phiên nằm trong `.codex/local/`.

Khi thêm trong PR liên quan, ghi ngắn: tình huống → bằng chứng → cách xử lý → kiểm tra và phạm vi áp dụng.

- **Cánh cửa bake bị static batching (18/10/2026):** mở cửa thì trạng thái/collider đổi nhưng hình cánh đứng yên; người chơi trông như đi xuyên cửa. Nguyên nhân: `PrepareStatic` gắn `BatchingStatic` cho mọi MeshRenderer, kể cả cánh cửa. Sửa: phần dưới `DoorView.leaf` chỉ `ContributeGI | ReflectionProbeStatic`. Kiểm tra: PlayMode `OpenedDoorLeafVisiblySwingsAwayFromPlayer` (leaf không `isPartOfStaticBatch`, tâm mesh dịch > 0.25 m). Áp dụng cho mọi phần chuyển động trong package.
- **Khe lan can hẹp hơn capsule (18/10/2026):** bot tự chơi không xuống được cầu thang từ L2 vì khe giữa lan can và tường 0.5 m < 0.56 m đường kính nhân vật; các test teleport không phát hiện. Cần kiểm tra đường đi bằng bot đi bộ thật cho mọi lối thoát.
- **Prefab VFX lưu nhầm GameObject bọc ngoài (18/10/2026):** `Flame.prefab` không có `ParticleSystem` ở gốc nên `RuntimeAssets.FirePrefab = null` và game im lặng rơi về lửa dựng lúc chạy không texture; ảnh chụp vẫn "có lửa" nhưng chỉ là chấm tròn. Kiểm tra bằng `prefab.GetComponent<ParticleSystem>() != null` sau khi dựng; `FireVisualTests` kiểm tra lớp haze/tàn lửa tồn tại.
- **Quad billboard tâm tại điểm sinh (18/10/2026):** nửa dưới (phần sáng nhất) của ngọn lửa chìm dưới sàn và bị soft particles làm mờ, lửa trông dẹt như vệt sáng. Dời gốc phát lên nửa chiều cao (`lift` trong `VfxFactory.Cone`).
- **`unity command eval` giới hạn 5 giây cho luồng chính (18/10/2026):** việc nhanh (đặt cài đặt, dựng/bake scene, chụp ảnh) vẫn chạy xong, nhưng `BuildAssetBundles` do `BuildWorker.Build` gọi bị ngắt với "Error building Player". Chạy worker thật bằng batchmode (đóng Editor) hoặc `Run-LocalTest.ps1`, dùng eval chỉ để xem scene/bake.
