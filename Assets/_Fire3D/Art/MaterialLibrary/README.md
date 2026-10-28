# Thư viện chất liệu (MaterialLibrary)

Texture PBR dùng chung cho mọi tòa nhà. GLB từ Blender worker chỉ mang `libraryKey`; Unity build worker gán material URP theo mã này trước khi bake, và thư viện được đóng thành AssetBundle dùng chung cài sẵn trong app (Docs `ifc_ingestion_visual_interaction.md` mục 5.5).

`Source/<libraryKey>/` chứa bản 1K-JPG đã tuyển: `Color`, `NormalGL`, `Roughness`, có thể có `AmbientOcclusion`, `Metalness`. Ảnh lưu bằng Git LFS.

| libraryKey | Nguồn ambientCG | Lặp (m) |
| --- | --- | --- |
| floor.tile | Tiles074 | 1.2 |
| floor.wood | WoodFloor051 | 2.0 |
| floor.carpet | Carpet016 | 1.0 |
| ceiling.tile | OfficeCeiling001 | 1.2 |
| wall.plaster | PaintedPlaster017 | 2.0 |
| wall.brick | Bricks097 | 1.5 |
| wall.tile | Tiles010 | 1.0 |
| concrete | Concrete034 | 2.0 |
| wood | Wood092 | 1.0 |
| stone | Marble012 | 1.5 |
| metal.painted | Metal027 | 1.0 |
| metal.brushed | Metal009 | 0.5 |
| floor.epoxy | Concrete048 | 2.0 |
| floor.vinyl | Plastic011 | 1.0 |
| floor.terrazzo | Tiles040 | 1.2 |
| floor.rubber | Rubber004 | 0.6 |
| wall.tile.white | Tiles133A | 0.6 |
| wall.paint | Paint004 | 2.0 |
| metal.stainless | Metal012 | 0.5 |

Biến thể: `Source/<libraryKey>@2`, `@3`… là bộ texture khác cho cùng khóa (cùng kích thước lặp); build worker chọn biến thể theo hash của revision + tên vật liệu IFC nên mỗi tòa một dáng, build lại vẫn giống. Hiện có: floor.tile@2 Tiles078; floor.wood@2 WoodFloor043, @3 WoodFloor040; floor.carpet@2 Carpet012; ceiling.tile@2 OfficeCeiling002, @3 OfficeCeiling003; wall.paint@2 Paint005; concrete@2 Concrete031, @3 Concrete030; floor.vinyl@2 Plastic010, @3 Plastic013A; wall.brick@2 Bricks105.

Giấy phép: mọi texture lấy từ [ambientCG](https://ambientcg.com) theo **CC0 1.0** (public domain), được dùng, sửa và phân phối lại không cần ghi công. Không đưa file IFC mẫu của spike vào repo vì giấy phép chưa rõ.
