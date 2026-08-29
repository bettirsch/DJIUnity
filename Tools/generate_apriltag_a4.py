from pathlib import Path

from PIL import Image
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.pdfgen import canvas


A4_WIDTH_MM = 210
A4_HEIGHT_MM = 297
PAGE_DPI = 300
DEFAULT_TAG_SIZE_MM = 200


def mm_to_px(mm_value: float, dpi: int) -> int:
    return round((mm_value / 25.4) * dpi)


def build_outputs(tag_png_path: Path, output_dir: Path, tag_size_mm: float = DEFAULT_TAG_SIZE_MM) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)

    page_width_px = mm_to_px(A4_WIDTH_MM, PAGE_DPI)
    page_height_px = mm_to_px(A4_HEIGHT_MM, PAGE_DPI)
    tag_size_px = mm_to_px(tag_size_mm, PAGE_DPI)

    a4_png_path = output_dir / "apriltag_a4_tagStandard41h12_id0_200mm.png"
    a4_pdf_path = output_dir / "apriltag_a4_tagStandard41h12_id0_200mm.pdf"
    notes_path = output_dir / "README.txt"

    tag_image = Image.open(tag_png_path).convert("L")
    resized_tag = tag_image.resize((tag_size_px, tag_size_px), resample=Image.Resampling.NEAREST)

    page_image = Image.new("L", (page_width_px, page_height_px), color=255)
    offset_x = (page_width_px - tag_size_px) // 2
    offset_y = (page_height_px - tag_size_px) // 2
    page_image.paste(resized_tag, (offset_x, offset_y))
    page_image.save(a4_png_path)

    pdf = canvas.Canvas(str(a4_pdf_path), pagesize=A4)
    pdf.drawInlineImage(
        str(a4_png_path),
        (A4_WIDTH_MM - tag_size_mm) * 0.5 * mm,
        (A4_HEIGHT_MM - tag_size_mm) * 0.5 * mm,
        width=tag_size_mm * mm,
        height=tag_size_mm * mm,
    )
    pdf.showPage()
    pdf.save()

    notes_path.write_text(
        "\n".join(
            [
                "AprilTag MVP printable marker",
                "Family: tagStandard41h12",
                "Tag ID: 0",
                f"Printed tag square size: {tag_size_mm:.0f} mm",
                "Print at 100% scale / actual size.",
                "Disable printer 'fit to page' or automatic scaling.",
            ]
        ),
        encoding="utf-8",
    )


if __name__ == "__main__":
    project_root = Path(__file__).resolve().parents[1]
    workspace_root = project_root.parent
    tag_png = workspace_root / "_vendor_apriltag_imgs" / "tagStandard41h12" / "tag41_12_00000.png"
    output = project_root / "Docs" / "AprilTag"
    build_outputs(tag_png, output)
