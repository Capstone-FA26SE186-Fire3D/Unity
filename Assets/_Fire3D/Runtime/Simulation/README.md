# Runtime/Simulation

Mô phỏng ECS chia theo tính năng. Mỗi module tạo Components/ (dữ liệu), Systems/ (điều phối) và Jobs/ (tính toán) khi có code thực tế. Jobs phải quản lý dependency và lifetime bộ nhớ; dùng EntityCommandBuffer cho structural changes. Presentation và native bridge không chạy trong job. Xem skill unity-ecs-patterns khi triển khai và đối chiếu API với phiên bản Entities được chọn.
