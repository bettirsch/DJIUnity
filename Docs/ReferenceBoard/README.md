# DJI Reference Board

`DjiReferenceBoard_LongRangeV2_AprilTagsOnly_360mm.pdf` is the maximized LONG-RANGE V2 physical board used by DJI localization and the experimental full-board phone reference. The older hybrid board remains a historical artifact and is not compatible with the maximized DJI marker layout.

- Finished board: 360 mm x 360 mm.
- Central BuildingReference artwork: absent.
- Four AprilTags: `tagStandard41h12`, IDs 0-3, 170 mm full-tile side length and 94.444 mm detector-corner side length.
- Marker centers: `(-90,+90)`, `(+90,+90)`, `(-90,-90)`, `(+90,-90)` mm in board coordinates.
- DJI tags: `tagStandard41h12` IDs 0, 1, 2, and 3; each printed tag's
  detector-corner pose quadrilateral is exactly 94.444 mm per side.
- Coordinate origin: board center.
- `+X`: right across the printed side; `+Y`: up; `+Z`: outward from the printed side.

Print the PDF at **100% / actual size**. Do not use fit-to-page scaling. A
360 mm square board exceeds A4, so use the supplied A4 tiled PDF or a larger
format printer. Verify each full tile measures 170 mm, the outer margin is
5 mm, the tile gap is 10 mm, and each AprilTag detector-corner side measures
94.444 mm before use. The PDF also includes a 100 mm
verification line along the bottom center; measure it before use.

Expected DJI tag scale is explanatory only, not a detector threshold:

| Distance | Approximate 94.444 mm detector quad |
| --- | --- |
| 2 m | 63 px |
| 3 m | 42 px |
| 4 m | 31 px |
| 5 m | 25 px |
| 6 m | 21 px |

These values use `tagPixels ~= fx * tagSize / distance` with `fx ~= 1332.7 px`.

Regenerate after changing board dimensions or marker IDs:

```powershell
python Tools/GenerateDjiReferenceBoard.py
```
