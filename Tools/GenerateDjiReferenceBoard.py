"""Generate the maximized Long-Range V2 AprilTag-only board from one manifest."""

import argparse
import json
import math
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageDraw
from reportlab.lib.units import mm
from reportlab.pdfgen import canvas


PRINT_DPI = 300
MANIFEST_RELATIVE_PATH = Path("Docs") / "ReferenceBoard" / "LongRangeV2.geometry.json"
APRILTAG_ONLY = "APRILTAG_ONLY"


@dataclass(frozen=True)
class TagPlacement:
    marker_id: str
    center_px: tuple[int, int]
    bounds_px: tuple[int, int, int, int]


@dataclass(frozen=True)
class BoardArtifact:
    variant: str
    png_path: Path
    pdf_path: Path
    image_size_px: tuple[int, int]
    tag_placements: tuple[TagPlacement, ...]


@dataclass(frozen=True)
class A4Tile:
    column: int
    row: int
    x_m: float
    y_m: float
    width_m: float
    height_m: float


def millimeters_to_pixels(value: float) -> int:
    return round(value / 25.4 * PRINT_DPI)


def meters_to_millimeters(value: float) -> float:
    return value * 1000.0


def load_geometry(manifest_path: Path) -> dict:
    with manifest_path.open("r", encoding="utf-8") as source:
        geometry = json.load(source)

    if geometry.get("version") != "LONG_RANGE_V2":
        raise ValueError(f"Unexpected board manifest version: {geometry.get('version')}")
    convention = geometry["coordinateConvention"]
    if (convention["origin"], convention["xAxis"], convention["yAxis"], convention["zAxis"]) != (
        "physical board center", "print-right", "print-up", "outward from printed surface"
    ):
        raise ValueError("Unexpected board coordinate convention.")
    if geometry["detectorQuad"]["widthAtBorderModules"] <= 0 or geometry["detectorQuad"]["totalWidthModules"] <= 0:
        raise ValueError("Detector-quad module counts must be positive.")
    return geometry


def board_position_to_pixel(center_m: dict[str, float], board_size_m: float) -> tuple[int, int]:
    # Board +X is print-right and +Y is print-up; Pillow's Y grows downward.
    half = board_size_m * 0.5
    x = millimeters_to_pixels(meters_to_millimeters(half + center_m["x"]))
    y = millimeters_to_pixels(meters_to_millimeters(half - center_m["y"]))
    return x, y


def validate_layout(geometry: dict) -> None:
    board = geometry["board"]
    printed_tag = geometry["printedAprilTagTile"]
    layout = geometry["printLayout"]
    markers = geometry["markers"]
    detector = geometry["detectorQuad"]

    expected_offset = board["widthM"] * 0.5 - layout["outerMarginM"] - printed_tag["widthM"] * 0.5
    assert all(math.isclose(abs(marker["centerM"]["x"]), expected_offset, abs_tol=1e-9) for marker in markers)
    assert all(math.isclose(abs(marker["centerM"]["y"]), expected_offset, abs_tol=1e-9) for marker in markers)
    assert math.isclose(
        2 * printed_tag["widthM"] + layout["innerGapM"] + 2 * layout["outerMarginM"],
        board["widthM"],
        abs_tol=1e-9,
    )
    assert math.isclose(
        printed_tag["widthM"] * detector["widthAtBorderModules"] / detector["totalWidthModules"],
        detector["sizeM"],
        abs_tol=1e-9,
    )
    assert [marker["id"] for marker in markers] == ["0", "1", "2", "3"]
    assert all(marker["printedRotationDegrees"] == 0 for marker in markers)
    assert math.isclose(geometry["markerCenterSpacingM"] - printed_tag["widthM"], layout["innerGapM"], abs_tol=1e-9)

    bounds = []
    for marker in markers:
        center = marker["centerM"]
        half_size = printed_tag["widthM"] * 0.5
        left, right = center["x"] - half_size, center["x"] + half_size
        bottom, top = center["y"] - half_size, center["y"] + half_size
        assert left >= -board["widthM"] * 0.5 and right <= board["widthM"] * 0.5
        assert bottom >= -board["heightM"] * 0.5 and top <= board["heightM"] * 0.5
        bounds.append((left, bottom, right, top))
    for index, first in enumerate(bounds):
        for second in bounds[index + 1:]:
            assert max(first[0], second[0]) >= min(first[2], second[2]) or max(first[1], second[1]) >= min(first[3], second[3])


def validate_tag_source(tag: Image.Image, geometry: dict, marker_id: int) -> None:
    detector = geometry["detectorQuad"]
    expected_size = detector["totalWidthModules"]
    if tag.size != (expected_size, expected_size):
        raise ValueError(f"AprilTag ID {marker_id} must be an untrimmed {expected_size}x{expected_size} source tile.")

    # tagStandard41h12 is reversed-border. Its 9x9 artwork carries the required
    # white ring around the 5-module detector quad internally; no exterior crop
    # or quiet-zone trimming is permitted before scaling the complete tile.
    border_width = detector["widthAtBorderModules"]
    start = (expected_size - border_width) // 2
    required_white = []
    for index in range(border_width - 1):
        required_white.extend(((start + index, start), (expected_size - 1 - start, start + index),
                               (start + index + 1, expected_size - 1 - start), (start, start + index + 1)))
    if any(tag.getpixel(point) != (255, 255, 255) for point in required_white):
        raise ValueError(f"AprilTag ID {marker_id} lacks the tagStandard41h12 internal white border; refusing to tile it edge-to-edge.")


def variant_file_stem() -> str:
    return "DjiReferenceBoard_LongRangeV2_AprilTagsOnly_360mm"


def draw_marker_artwork(board: Image.Image, project_root: Path, geometry: dict) -> tuple[TagPlacement, ...]:
    workspace_root = project_root.parent
    tag_dir = workspace_root / "_vendor_apriltag_imgs" / "tagStandard41h12"
    board_size_m = geometry["board"]["widthM"]
    tag_size_px = millimeters_to_pixels(meters_to_millimeters(geometry["printedAprilTagTile"]["widthM"]))
    draw = ImageDraw.Draw(board)
    placements = []

    for marker in geometry["markers"]:
        marker_id = int(marker["id"])
        if marker["printedRotationDegrees"] != 0:
            raise ValueError("The current generator supports only upright printed AprilTags.")
        tag_path = tag_dir / f"tag41_12_{marker_id:05d}.png"
        source_tag = Image.open(tag_path).convert("RGB")
        validate_tag_source(source_tag, geometry, marker_id)
        tag = source_tag.resize((tag_size_px, tag_size_px), Image.Resampling.NEAREST)
        center_px = board_position_to_pixel(marker["centerM"], board_size_m)
        origin = (center_px[0] - tag_size_px // 2, center_px[1] - tag_size_px // 2)
        bounds = (origin[0], origin[1], origin[0] + tag_size_px, origin[1] + tag_size_px)
        board.paste(tag, origin)
        draw.rectangle((origin[0], origin[1], origin[0] + tag_size_px - 1, origin[1] + tag_size_px - 1), outline="black", width=2)
        placements.append(TagPlacement(marker["id"], center_px, bounds))

    return tuple(placements)


def write_print_artifact(png_path: Path, pdf_path: Path, geometry: dict) -> None:
    board_size_mm = meters_to_millimeters(geometry["board"]["widthM"])
    ruler = geometry["printLayout"]["verificationRuler"]
    pdf = canvas.Canvas(str(pdf_path), pagesize=(board_size_mm * mm, board_size_mm * mm))
    pdf.drawInlineImage(str(png_path), 0, 0, width=board_size_mm * mm, height=board_size_mm * mm)
    ruler_start_mm = meters_to_millimeters(ruler["startXM"])
    ruler_end_mm = meters_to_millimeters(ruler["endXM"])
    ruler_y_mm = meters_to_millimeters(ruler["yM"])
    pdf.setLineWidth(0.35)
    pdf.line(ruler_start_mm * mm, ruler_y_mm * mm, ruler_end_mm * mm, ruler_y_mm * mm)
    pdf.line(ruler_start_mm * mm, 1 * mm, ruler_start_mm * mm, 4 * mm)
    pdf.line(ruler_end_mm * mm, 1 * mm, ruler_end_mm * mm, 4 * mm)
    pdf.setFont("Helvetica", 5)
    pdf.drawCentredString((ruler_start_mm + ruler_end_mm) * 0.5 * mm, 4.5 * mm, "100 mm verification line")
    pdf.showPage()
    pdf.save()


def build_a4_tile_plan(geometry: dict, overlap_m: float | None) -> tuple[dict, tuple[A4Tile, ...]]:
    tiled = geometry["printLayout"]["a4TiledPrint"]
    board = geometry["board"]
    columns = tiled["columns"]
    rows = tiled["rows"]
    overlap_m = tiled["overlapM"] if overlap_m is None else overlap_m
    if columns < 1 or rows < 1 or overlap_m < 0:
        raise ValueError("A4 tiled-print dimensions must be positive and overlap cannot be negative.")

    tile_width_m = (board["widthM"] + (columns - 1) * overlap_m) / columns
    tile_height_m = (board["heightM"] + (rows - 1) * overlap_m) / rows
    if tile_width_m > tiled["pageWidthM"] or tile_height_m > tiled["pageHeightM"]:
        raise ValueError("A4 tile does not fit on its configured page at 1:1 scale.")

    tiles = tuple(
        A4Tile(
            column=column,
            row=row,
            x_m=column * (tile_width_m - overlap_m),
            y_m=row * (tile_height_m - overlap_m),
            width_m=tile_width_m,
            height_m=tile_height_m,
        )
        for row in range(rows)
        for column in range(columns)
    )
    if not math.isclose(tiles[-1].x_m + tile_width_m, board["widthM"], abs_tol=1e-9):
        raise AssertionError("A4 tiled-print width does not assemble to the board width.")
    if not math.isclose(tiles[-1].y_m + tile_height_m, board["heightM"], abs_tol=1e-9):
        raise AssertionError("A4 tiled-print height does not assemble to the board height.")
    return {**tiled, "overlapM": overlap_m}, tiles


def draw_a4_crop_marks(pdf: canvas.Canvas, x: float, y: float, width: float, height: float) -> None:
    mark_length = 3.0 * mm
    mark_offset = 1.5 * mm
    pdf.setLineWidth(0.35)
    for corner_x, horizontal_sign in ((x, -1), (x + width, 1)):
        for corner_y, vertical_sign in ((y, -1), (y + height, 1)):
            pdf.line(corner_x + horizontal_sign * mark_offset, corner_y, corner_x + horizontal_sign * (mark_offset + mark_length), corner_y)
            pdf.line(corner_x, corner_y + vertical_sign * mark_offset, corner_x, corner_y + vertical_sign * (mark_offset + mark_length))


def write_a4_tiled_print_artifact(
    png_path: Path,
    pdf_path: Path,
    geometry: dict,
    overlap_m: float | None,
) -> tuple[dict, tuple[A4Tile, ...]]:
    tiled, tiles = build_a4_tile_plan(geometry, overlap_m)
    board = geometry["board"]
    page_width = tiled["pageWidthM"] * 1000.0 * mm
    page_height = tiled["pageHeightM"] * 1000.0 * mm
    tile_width = tiles[0].width_m * 1000.0 * mm
    tile_height = tiles[0].height_m * 1000.0 * mm
    content_x = (page_width - tile_width) * 0.5
    content_y = (page_height - tile_height) * 0.5
    board_width = board["widthM"] * 1000.0 * mm
    board_height = board["heightM"] * 1000.0 * mm

    # invariant=1 avoids PDF creation-time metadata changing deterministic output.
    pdf = canvas.Canvas(str(pdf_path), pagesize=(page_width, page_height), invariant=1)
    for tile in tiles:
        path = pdf.beginPath()
        path.rect(content_x, content_y, tile_width, tile_height)
        pdf.saveState()
        pdf.clipPath(path, stroke=0, fill=0)
        # Draw the complete source once at physical 1:1 scale; clipping selects the tile.
        pdf.drawImage(
            str(png_path),
            content_x - tile.x_m * 1000.0 * mm,
            content_y - tile.y_m * 1000.0 * mm,
            width=board_width,
            height=board_height,
            mask="auto",
        )
        pdf.restoreState()

        draw_a4_crop_marks(pdf, content_x, content_y, tile_width, tile_height)
        pdf.setFont("Helvetica-Bold", 8)
        pdf.drawCentredString(
            page_width * 0.5,
            content_y + tile_height + 5.0 * mm,
            f"LONG-RANGE V2  A4 TILE  C{tile.column + 1}/{tiled['columns']}  R{tile.row + 1}/{tiled['rows']}",
        )
        pdf.setFont("Helvetica", 6)
        pdf.drawCentredString(
            page_width * 0.5,
            content_y + tile_height + 1.8 * mm,
            f"Board crop: X {tile.x_m * 1000:.1f}-{(tile.x_m + tile.width_m) * 1000:.1f} mm, "
            f"Y {tile.y_m * 1000:.1f}-{(tile.y_m + tile.height_m) * 1000:.1f} mm",
        )

        ruler_start = page_width * 0.5 - 50.0 * mm
        ruler_y = page_height - 17.0 * mm
        pdf.setLineWidth(0.5)
        pdf.line(ruler_start, ruler_y, ruler_start + 100.0 * mm, ruler_y)
        pdf.line(ruler_start, ruler_y - 2.0 * mm, ruler_start, ruler_y + 2.0 * mm)
        pdf.line(ruler_start + 100.0 * mm, ruler_y - 2.0 * mm, ruler_start + 100.0 * mm, ruler_y + 2.0 * mm)
        pdf.setFont("Helvetica-Bold", 7)
        pdf.drawCentredString(page_width * 0.5, ruler_y + 3.5 * mm, "MUST MEASURE 100 mm AFTER PRINTING")
        pdf.setFont("Helvetica", 6)
        pdf.drawCentredString(page_width * 0.5, 16.0 * mm, "Print at Actual Size / 100%. Disable Fit, Shrink, Scale-to-fit and Poster mode.")
        pdf.drawCentredString(page_width * 0.5, 10.0 * mm, "Verify the 100 mm calibration line before trimming, overlapping and taping pages.")
        pdf.showPage()
    pdf.save()
    return tiled, tiles


def validate_a4_tiling(source_png: Path, geometry: dict, tiled: dict, tiles: tuple[A4Tile, ...]) -> None:
    board = geometry["board"]
    with Image.open(source_png) as source:
        if source.size[0] != source.size[1]:
            raise AssertionError("A4 tiled-print source image is not square.")
    if not math.isclose(board["widthM"], board["heightM"], abs_tol=1e-9):
        raise AssertionError("A4 tiled-print expects the manifest's square board geometry.")
    if len(tiles) != tiled["columns"] * tiled["rows"]:
        raise AssertionError("A4 tiled-print page count does not match the manifest.")
    if any(tile.width_m > tiled["pageWidthM"] or tile.height_m > tiled["pageHeightM"] for tile in tiles):
        raise AssertionError("A4 tiled-print page clips a tile or rescales the board.")


def build_board(project_root: Path, geometry: dict, output_dir: Path) -> BoardArtifact:
    validate_layout(geometry)
    output_dir.mkdir(parents=True, exist_ok=True)
    board_size_m = geometry["board"]["widthM"]
    page_size_px = millimeters_to_pixels(meters_to_millimeters(board_size_m))
    board = Image.new("RGB", (page_size_px, page_size_px), "white")

    tag_placements = draw_marker_artwork(board, project_root, geometry)
    stem = variant_file_stem()
    png_path = output_dir / f"{stem}.png"
    pdf_path = output_dir / f"{stem}.pdf"
    board.save(png_path, dpi=(PRINT_DPI, PRINT_DPI))
    write_print_artifact(png_path, pdf_path, geometry)
    return BoardArtifact(APRILTAG_ONLY, png_path, pdf_path, board.size, tag_placements)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, help="Override the Long-Range V2 geometry manifest path.")
    parser.add_argument("--output-dir", type=Path, help="Write generated artifacts outside Docs/ReferenceBoard.")
    parser.add_argument("--tile-overlap-mm", type=float, help="Override the manifest A4 tile overlap in millimeters.")
    parser.add_argument("--skip-a4-tiled", action="store_true", help="Do not generate exact-scale A4 tiled PDFs.")
    arguments = parser.parse_args()

    root = Path(__file__).resolve().parents[1]
    manifest_path = arguments.manifest or root / MANIFEST_RELATIVE_PATH
    output_dir = arguments.output_dir or root / "Docs" / "ReferenceBoard"
    geometry = load_geometry(manifest_path)
    artifact = build_board(root, geometry, output_dir)
    print(f"Generated {artifact.variant} PNG {artifact.png_path}")
    print(f"Generated {artifact.variant} PDF {artifact.pdf_path}")
    if not arguments.skip_a4_tiled:
        tiled_pdf_path = output_dir / f"{variant_file_stem()}_A4_Tiled.pdf"
        overlap_m = arguments.tile_overlap_mm / 1000.0 if arguments.tile_overlap_mm is not None else None
        tiled, tiles = write_a4_tiled_print_artifact(artifact.png_path, tiled_pdf_path, geometry, overlap_m)
        validate_a4_tiling(artifact.png_path, geometry, tiled, tiles)
        print(
            f"A4_TILED_PRINT_VALIDATION=PASS variant={artifact.variant} pages={len(tiles)} "
            f"layout={tiled['columns']}x{tiled['rows']} overlapM={tiled['overlapM']:.3f} scale=1:1"
        )
        print(f"Generated {artifact.variant} A4 tiled PDF {tiled_pdf_path}")
