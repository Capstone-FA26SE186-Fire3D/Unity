# IFC geometry worker

Chuyển một tệp IFC thành `building.glb` + metadata cho editor web và Unity build worker. Chạy theo pull mode trên VM worker (cạnh `Tools/PackageWorker`), không dùng LLM/RAG. Hợp đồng BE: `BE/docs/geometry-worker.md`; thiết kế: `Docs/ifc_ingestion_visual_interaction.md` mục 5.10.

## Đầu ra

| Tệp | Artifact BE | Nội dung |
| --- | --- | --- |
| `building.glb` | `preview_glb` | Node gốc = tầng (`L1..Ln` theo cao độ), node con = phần tử, tên = IFC GlobalId. Vị trí tuyệt đối, mét, Y lên, tay phải. Material `FET3D_<libraryKey>`, UV0 chiếu hộp theo mét, không texture. |
| `preview_metadata.json` | metadata của `preview_glb` | `fet3d.editor/1`: `coordinateTransform` (IFC → GLB, column-major, gồm đơn vị và gốc), `floors`, `semanticMapping`. |
| `building_metadata.json` | `building_metadata` | `fet3d.building/1`: `storeys`, `spaces` (bbox tuyệt đối), `portals` (cửa + cầu thang `type: "stair"`), `interactables` (ứng viên thiết bị, vị trí theo tầng), `materials`, `qaIssues`. |
| `result.json` | — | Tóm tắt: `outcome` Passed/Failed, số tầng/phần tử/tam giác, `issues`. |

Mọi tọa độ trong hợp đồng dùng hệ GLB; Unity đảo dấu X khi nạp (`ContractFrame`). Thiếu dữ liệu chỉ thành QA issue: không tự sinh sàn, tường hay cửa thoát.

## Cài trên VM

```powershell
cd C:\fet3d\unity\Tools\GeometryWorker
.\Install-GeometryWorker.ps1 -BaseUrl https://api.fet3d.io.vn -RegisterTask
```

Cần Python 3.11 x64 (`py -3.11`). Script tạo `.venv` với `requirements.txt` (phiên bản cố định), dùng lại khóa DPAPI của package worker nếu đã có (`-SetKey` để nhập/đổi khóa) và đăng ký Scheduled Task chạy lúc VM khởi động. Log: `C:\fet3d\geometry-work\logs\geometry-<ngày>.log`. Backend bật bằng `ProcessingWorker:GeometryPullEnabled=true` và thêm `fet3d-geometry-worker-1` vào `AllowedToolchains`.

## Chạy tay và kiểm thử

```powershell
.\.venv\Scripts\python.exe run_converter.py convert --ifc model.ifc --out out
.\.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
.\.venv\Scripts\python.exe -m pytest -q tests
.\Test\Run-LocalTest.ps1 -Ifc model.ifc -ExpireFirstUrl   # agent + fake backend
.\Test\Run-LocalTest.ps1 -Ifc x -Broken                   # đường fail
python Test\make_scenario.py out --spawn-space A203 --exit-door <GlobalId>  # scenario thử cho BuildWorker
```

`tests/schemas` là bản sao schema của `BE/contracts` (editor v1, ifc v1); cập nhật khi BE đổi hợp đồng.

## Giấy phép

IfcOpenShell: LGPL-3.0-or-later (dùng như thư viện, không sửa). Không đưa IFC mẫu bên thứ ba vào repo.
