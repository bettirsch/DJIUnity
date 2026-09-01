# DJI Reference Board

`DjiReferenceBoard_360mm.pdf` is the physical board used by both scenes.

- Finished board: 360 mm x 360 mm.
- Center image: `BuildingReference`, 180 mm x 180 mm.
- DJI tags: `tagStandard41h12` IDs 0, 1, 2, and 3, each 60 mm square.
- Coordinate origin: board center.
- `+X`: right across the printed side; `+Y`: up; `+Z`: outward from the printed side.

Print the PDF at **100% / actual size**. Do not use fit-to-page scaling. A
360 mm square board exceeds A4, so use a printer's poster/tile mode or a larger
format printer. Verify the printed tag outer squares measure 60 mm and the
central image measures 180 mm before use.

Regenerate after changing board dimensions or marker IDs:

```powershell
python Tools/GenerateDjiReferenceBoard.py
```
