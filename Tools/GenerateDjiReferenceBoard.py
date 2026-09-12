"""Generate the physical board shared by phone AR tracking and DJI vision.

The output is a 360 mm square PDF. Print it at 100% scale, or use a poster
printing mode that preserves its physical dimensions across multiple pages.
"""

from pathlib import Path

from PIL import Image, ImageDraw
from reportlab.lib.units import mm
from reportlab.pdfgen import canvas


BOARD_SIZE_MM = 360
PHONE_IMAGE_SIZE_MM = 180
TAG_SIZE_MM = 60
TAG_CENTERS_MM = {
    0: (-135, 135),
    1: (135, 135),
    2: (-135, -135),
    3: (135, -135),
}
PRINT_DPI = 300


def millimeters_to_pixels(value: float) -> int:
    return round(value / 25.4 * PRINT_DPI)


def board_position_to_pixel(center_mm: tuple[float, float], image_size_px: int) -> tuple[int, int]:
    # Board +X is print-right and +Y is print-up; Pillow's Y grows downward.
    half = BOARD_SIZE_MM * 0.5
    x = millimeters_to_pixels(half + center_mm[0])
    y = millimeters_to_pixels(half - center_mm[1])
    return x, y


def build_board(project_root: Path) -> tuple[Path, Path]:
    workspace_root = project_root.parent
    tag_dir = workspace_root / "_vendor_apriltag_imgs" / "tagStandard41h12"
    phone_image_path = project_root / "Assets" / "AR" / "ReferenceImages" / "BuildingReference.png"
    output_dir = project_root / "Docs" / "ReferenceBoard"
    output_dir.mkdir(parents=True, exist_ok=True)

    page_size_px = millimeters_to_pixels(BOARD_SIZE_MM)
    board = Image.new("RGB", (page_size_px, page_size_px), "white")
    draw = ImageDraw.Draw(board)

    phone_size_px = millimeters_to_pixels(PHONE_IMAGE_SIZE_MM)
    phone_image = Image.open(phone_image_path).convert("RGB").resize(
        (phone_size_px, phone_size_px), Image.Resampling.LANCZOS
    )
    phone_origin = ((page_size_px - phone_size_px) // 2, (page_size_px - phone_size_px) // 2)
    board.paste(phone_image, phone_origin)

    tag_size_px = millimeters_to_pixels(TAG_SIZE_MM)
    for marker_id, center_mm in TAG_CENTERS_MM.items():
        tag_path = tag_dir / f"tag41_12_{marker_id:05d}.png"
        tag = Image.open(tag_path).convert("RGB").resize((tag_size_px, tag_size_px), Image.Resampling.NEAREST)
        center_px = board_position_to_pixel(center_mm, page_size_px)
        origin = (center_px[0] - tag_size_px // 2, center_px[1] - tag_size_px // 2)
        board.paste(tag, origin)
        draw.rectangle((origin[0], origin[1], origin[0] + tag_size_px - 1, origin[1] + tag_size_px - 1), outline="black", width=2)

    png_path = output_dir / "DjiReferenceBoard_360mm.png"
    pdf_path = output_dir / "DjiReferenceBoard_360mm.pdf"
    board.save(png_path, dpi=(PRINT_DPI, PRINT_DPI))

    pdf = canvas.Canvas(str(pdf_path), pagesize=(BOARD_SIZE_MM * mm, BOARD_SIZE_MM * mm))
    pdf.drawInlineImage(str(png_path), 0, 0, width=BOARD_SIZE_MM * mm, height=BOARD_SIZE_MM * mm)
    pdf.showPage()
    pdf.save()
    return png_path, pdf_path


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[1]
    png, pdf = build_board(root)
    print(f"Generated {png}")
    print(f"Generated {pdf}")
