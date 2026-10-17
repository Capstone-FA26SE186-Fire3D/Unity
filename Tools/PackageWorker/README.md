# FET3D Unity package worker (agent)

Agent chạy trên VM Windows của BE (BE#110, `UNITY-WORKER`), kéo job đóng gói từ backend theo hợp đồng pull-mode
(BE `docs/unity-package-worker.md`), chạy Unity Editor batchmode (`BuildWorker.Run`) và đẩy gói lên S3 qua URL ký sẵn.
VM chỉ cần kết nối ra ngoài; không mở cổng vào, không giữ khóa S3/DB.

```
lease ──► tải input (SHA-256) ──► scenario.json ──► Unity.exe -batchmode BuildWorker.Run ──► upload-urls ──► PUT từng file
  │            (renew lease mỗi ~100 s trong lúc Unity chạy)                                  │
  └── 204: chờ pollSeconds                                           outputs (manifest + unity_package) ──► complete
```

Toàn bộ viết cho **Windows PowerShell 5.1** (có sẵn trên Windows Server 2022, không cần pwsh 7).

| File | Vai trò |
| --- | --- |
| `Fire3DPackageWorker.ps1` | Vòng lặp worker. `-Once` xử lý tối đa một job rồi thoát (dùng khi test). |
| `Install-PackageWorker.ps1` | Tạo thư mục, ghi `worker.config.json`, lưu khóa `X-Worker-Key` bằng DPAPI, đăng ký Scheduled Task. |
| `worker.config.example.json` | Mẫu cấu hình. `worker.config.json` thật không commit. |
| `Test/Run-LocalTest.ps1` | Test end-to-end trên máy dev: export graybox làm input → fake backend → chạy worker một job → kiểm tra. |
| `Test/FakeBackend.py` | Backend giả (Python 3) mô phỏng lease/renew/inputs/upload-urls/outputs/complete/fail và S3 (kiểm tra `x-amz-checksum-sha256`). |

## Cài trên VM (một lần)

Điều kiện: Unity 6000.6.6f1 + Android Build Support đã cài qua Unity Hub và **đã đăng nhập license bằng chính user sẽ chạy worker**
(batchmode dùng license của user đó); Git + Git LFS; project đã clone.

```powershell
# 1. Clone project (nhánh main) – khóa trong Credential Manager của git nếu repo private
git clone https://github.com/Capstone-FA26SE186-Fire3D/Unity.git C:\fet3d\unity
git -C C:\fet3d\unity lfs pull

# 2. Import lần đầu (tạo Library, kiểm tra compile) – vài phút
& "C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe" -batchmode -quit -projectPath C:\fet3d\unity -logFile C:\fet3d\first-import.log

# 3. Cài agent: hỏi khóa X-Worker-Key (nhập ẩn), ghi config, đăng ký Scheduled Task và chạy luôn
Set-Location C:\fet3d\unity\Tools\PackageWorker
.\Install-PackageWorker.ps1 -BaseUrl https://api.fet3d.io.vn -RegisterTask
```

Khóa `X-Worker-Key` do BE cấp (32–512 ký tự, cùng giá trị `ProcessingWorker__MachineKey` cho tới khi BE tách khóa riêng cho VM).
Script lưu khóa ở `C:\fet3d\secrets\worker-key.dpapi` (DPAPI, chỉ user đó giải mã được; ACL chỉ user + Administrators).
Không bao giờ dán khóa vào config, script, issue hay log. Đổi khóa: chạy lại `.\Install-PackageWorker.ps1 -KeyOnly`.

BE cần bật trên deployment: `ProcessingWorker__PackagePullEnabled=true`, `ProcessingWorker__AllowedToolchains__0=unity-6000.6.6f1-worker-1`
(trùng `toolchainVersion` trong config), `PackageLeaseSeconds` 300.

### Scheduled Task

`-RegisterTask` tạo task **FET3D Unity Package Worker** (RunLevel Limited, tự chạy lại sau 1 phút nếu tiến trình chết, một instance).
Chế độ đăng nhập `-TaskLogon`:

- `Password` (mặc định): worker **tự chạy khi VM khởi động** (bấm Start trên Azure Portal là đủ, không cần RDP), trễ 1 phút để mạng sẵn sàng. Windows tự hiện hộp thoại hỏi mật khẩu tài khoản và giữ trong Task Scheduler; FET3D không đụng tới mật khẩu. Đổi mật khẩu Windows thì chạy lại installer.
- `Interactive`: chỉ chạy khi user đăng nhập (phải RDP vào một lần sau mỗi lần bật VM). Ngắt RDP bằng nút X thì worker vẫn chạy; Sign out thì dừng.
- Không dùng S4U: phiên S4U không giải mã được khóa DPAPI và không đọc được Git Credential Manager.

Tắt VM để tiết kiệm: dùng **Stop** trên Azure Portal (trạng thái phải là *Stopped (deallocated)*). Job tạo trong lúc VM tắt nằm chờ; job đang build dở được nhận lại khi lease hết hạn sau khi worker chạy lại.

Quản lý: `Start-ScheduledTask`/`Stop-ScheduledTask -TaskName 'FET3D Unity Package Worker'`. Chạy tay để xem log trực tiếp:
`powershell -NoProfile -ExecutionPolicy Bypass -File .\Fire3DPackageWorker.ps1`.

## Cấu hình (`worker.config.json`)

| Khóa | Mặc định | Ý nghĩa |
| --- | --- | --- |
| `baseUrl` | – | API backend, ví dụ `https://api.fet3d.io.vn` |
| `toolchainVersion` | `unity-6000.6.6f1-worker-1` | Phải nằm trong `AllowedToolchains` của BE |
| `buildTarget` | `Android` | Gửi khi lease và bắt buộc trùng `manifest.buildTarget` |
| `unityExe`, `projectPath` | `C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe`, `C:\fet3d\unity` | Giữ Unity và project trên ổ C (ổ D của Azure là đĩa tạm) |
| `workRoot` | `C:\fet3d\work` | `jobs\<jobId>-<attempt>\` (unity.log, worker.log, result.json, output) và `logs\worker-<ngày>.log` |
| `keyFile` | `C:\fet3d\secrets\worker-key.dpapi` | Khóa DPAPI do installer ghi |
| `pollSeconds` / `renewSeconds` | 30 / 100 | Chu kỳ hỏi job khi rảnh / gia hạn lease (≈ `PackageLeaseSeconds`/3) |
| `buildTimeoutMinutes` | 120 | Quá hạn thì kill Unity và fail job |
| `gpuBake` | false | Thêm `-fet3dGpu` (VM D4s không có GPU: để false) |
| `noBake` | false | Bỏ bake ánh sáng (chỉ để debug) |
| `noGraphics` | false | Thêm `-nographics`; để false vì bake cần thiết bị đồ họa (WARP trên VM) |
| `gitPull` / `gitBranch` | true / `main` | Trước mỗi job: `git pull --ff-only` + `git lfs pull`; lỗi thì build bằng checkout hiện có |
| `keepJobs` | 5 | Số thư mục job giữ lại để soi log |

## Hành vi với backend

- Lease `204` → chờ `pollSeconds`. `409 PACKAGE_PULL_DISABLED` và `401` → ghi log, chờ tiếp.
- `inputs[]`: `preview_glb` → `building.glb`, `building_metadata` → `building_metadata.json`; kiểm tra size + SHA-256; URL hết hạn thì gọi
  `/inputs` ký lại một lần. `url` null, `inputs` rỗng, thiếu GLB/metadata → **fail** job với lý do.
- `scenarioSnapshot` → `scenario.json`; `requiredCapabilities` trong manifest = tập `capability.id` của các object trong scenario.
- Unity chạy với `-fet3dResult`: `result.json` (`ok`, `floors`, `warnings`, `issues`, `error`). Unity exit ≠ 0, không có result, manifest
  không qua `PackageManifestFactory.Validate` → fail với thông báo + 30 dòng log cuối (≤ 1000 ký tự).
- Renew trả `409 ATTEMPT_STALE` → dừng Unity, xóa output, **không** gọi fail (job thuộc attempt khác).
- Upload: `upload-urls` cho mọi file + `manifest.json`; PUT từng file (3 lần thử) với đúng header `x-amz-checksum-sha256`,
  `x-amz-sdk-checksum-algorithm`. Outputs: `manifest` (`schemaVersion fet3d.package/1`, metadata = manifest) rồi `unity_package`
  (metadata: buildTarget, protocolVersion, manifestSchemaVersion, minRuntimeVersion, requiredCapabilities + provenance). Complete với
  `outputHash` của lần outputs cuối; `409 PACKAGE_STORAGE_MISMATCH` → upload lại một lần; `5xx` → thử lại tối đa 6 lần.
- Log che query string của mọi URL ký sẵn và che khóa worker.

## Test trên máy dev

```powershell
# Nhanh: StandaloneWindows64, không bake (~3 phút, Unity compile + build bundle)
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test\Run-LocalTest.ps1 -UnityExe D:\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe

# Giống production: Android + bake 2 bộ ánh sáng
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test\Run-LocalTest.ps1 -Bake -BuildTarget Android
```

Test export scene graybox thành `building.glb` (một node cấp cao mỗi tầng `L1`, `L2`, tên node cửa = ifcGlobalId, material chỉ mang tên
`FET3D_<libraryKey>`), `building_metadata.json` (có `materials` ánh xạ `libraryKey`) và `scenario.json`; dựng fake backend; chạy
worker một job; kiểm tra backend đã nhận renew, 2 outputs, complete và file upload khớp SHA-256 trong manifest.
Gói kết quả nằm ở `%TEMP%\fet3d-worker-test\backend\out\<attemptId>\`.
