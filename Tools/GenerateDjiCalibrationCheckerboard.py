"""Generate the printable checkerboard for DJI ImageReader calibration."""

from pathlib import Path

from PIL import Image, ImageDraw
from reportlab.lib.pagesizes import landscape, A4
from reportlab.lib.units import mm
from reportlab.pdfgen import canvas


INNER_CORNERS_X = 9
INNER_CORNERS_Y = 6
SQUARES_X = INNER_CORNERS_X + 1
SQUARES_Y = INNER_CORNERS_Y + 1
SQUARE_SIZE_MM = 25
MARGIN_MM = 10
DPI = 300


def px(value_mm: float) -> int:
    return round(value_mm / 25.4 * DPI)


def generate(project_root: Path) -> tuple[Path, Path]:
    output = project_root / "Docs" / "DjiCameraCalibration"
    output.mkdir(parents=True, exist_ok=True)
    page_width_mm, page_height_mm = 297, 210
    image = Image.new("RGB", (px(page_width_mm), px(page_height_mm)), "white")
    draw = ImageDraw.Draw(image)

    board_width_mm = SQUARES_X * SQUARE_SIZE_MM
    board_height_mm = SQUARES_Y * SQUARE_SIZE_MM
    origin_x = px((page_width_mm - board_width_mm) * 0.5)
    origin_y = px((page_height_mm - board_height_mm) * 0.5)
    square_px = px(SQUARE_SIZE_MM)
    for row in range(SQUARES_Y):
        for column in range(SQUARES_X):
            if (row + column) % 2 == 0:
                left = origin_x + column * square_px
                top = origin_y + row * square_px
                draw.rectangle((left, top, left + square_px - 1, top + square_px - 1), fill="black")

    png = output / "DjiCalibrationCheckerboard_A4_25mm.png"
    pdf = output / "DjiCalibrationCheckerboard_A4_25mm.pdf"
    image.save(png, dpi=(DPI, DPI))
    document = canvas.Canvas(str(pdf), pagesize=landscape(A4))
    document.drawInlineImage(str(png), 0, 0, width=page_width_mm * mm, height=page_height_mm * mm)
    document.showPage()
    document.save()
    return png, pdf


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[1]
    png_path, pdf_path = generate(root)
    print(f"Generated {png_path}")
    print(f"Generated {pdf_path}")
