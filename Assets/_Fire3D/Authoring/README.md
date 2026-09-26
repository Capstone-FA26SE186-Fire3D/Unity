# Authoring

MonoBehaviour cấu hình Inspector và baker chuyển dữ liệu sang ECS. Khi cài Entities, tạo assembly Fire3D.Unity.Authoring riêng với reference Simulation/Contracts và package cần thiết; không đặt toàn bộ Authoring vào Editor-only vì authoring component có thể gắn vào asset. Custom inspector/UnityEditor API đặt ở Editor/.
