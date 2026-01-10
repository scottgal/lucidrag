#!/usr/bin/env python3
"""
Extract tables from PDF documents using pdfplumber.
Outputs JSON format for DocSummarizer consumption.
"""

import argparse
import json
import sys
from typing import List, Dict, Any, Optional

try:
    import pdfplumber
except ImportError:
    print(json.dumps({"error": "pdfplumber not installed. Run: pip install pdfplumber"}), file=sys.stderr)
    sys.exit(1)


def extract_tables_from_pdf(
    pdf_path: str,
    pages: Optional[List[int]] = None,
    min_rows: int = 2,
    min_cols: int = 2,
    enable_ocr: bool = False
) -> List[Dict[str, Any]]:
    """
    Extract tables from PDF using pdfplumber.

    Args:
        pdf_path: Path to PDF file
        pages: List of page numbers to process (1-indexed), None for all pages
        min_rows: Minimum rows to consider a table
        min_cols: Minimum columns to consider a table
        enable_ocr: Whether to use OCR for image-based PDFs

    Returns:
        List of table dictionaries with structure:
        {
            "page": int,
            "boundingBox": [x0, y0, x1, y1],
            "rows": [[cell1, cell2, ...], ...],
            "hasHeader": bool,
            "confidence": float,
            "metadata": {}
        }
    """
    extracted_tables = []

    with pdfplumber.open(pdf_path) as pdf:
        # Determine which pages to process
        if pages:
            # Convert to 0-indexed
            page_indices = [p - 1 for p in pages if 0 <= p - 1 < len(pdf.pages)]
        else:
            page_indices = range(len(pdf.pages))

        for page_idx in page_indices:
            page = pdf.pages[page_idx]
            page_num = page_idx + 1  # 1-indexed for output

            # Extract tables from this page
            tables = page.extract_tables()

            if not tables:
                continue

            for table_idx, table in enumerate(tables):
                if not table or len(table) < min_rows:
                    continue

                # Filter out empty rows
                non_empty_rows = [
                    row for row in table
                    if row and any(cell and str(cell).strip() for cell in row)
                ]

                if len(non_empty_rows) < min_rows:
                    continue

                # Check column count
                col_count = len(non_empty_rows[0]) if non_empty_rows else 0
                if col_count < min_cols:
                    continue

                # Clean cells (convert None to empty string, strip whitespace)
                cleaned_rows = []
                for row in non_empty_rows:
                    cleaned_row = [
                        str(cell).strip() if cell is not None else ""
                        for cell in row
                    ]
                    cleaned_rows.append(cleaned_row)

                # Detect if first row is header
                has_header = detect_header(cleaned_rows)

                # Estimate confidence based on table structure
                confidence = estimate_confidence(cleaned_rows, page)

                # Get table bounding box (if available)
                bbox = get_table_bbox(page, table_idx)

                extracted_table = {
                    "page": page_num,
                    "boundingBox": bbox,
                    "rows": cleaned_rows,
                    "hasHeader": has_header,
                    "confidence": confidence,
                    "metadata": {
                        "pageWidth": float(page.width),
                        "pageHeight": float(page.height),
                        "tableIndex": table_idx
                    }
                }

                extracted_tables.append(extracted_table)

    return extracted_tables


def detect_header(rows: List[List[str]]) -> bool:
    """
    Heuristic to detect if first row is a header.
    """
    if len(rows) < 2:
        return False

    first_row = rows[0]

    # Check if first row cells are non-numeric (common for headers)
    non_numeric_count = sum(1 for cell in first_row if not is_numeric(cell))

    # If most cells in first row are non-numeric, likely a header
    return non_numeric_count >= len(first_row) * 0.6


def is_numeric(value: str) -> bool:
    """Check if string represents a number."""
    if not value or not value.strip():
        return False

    # Remove common formatting
    cleaned = value.replace(',', '').replace('$', '').replace('%', '').strip()

    try:
        float(cleaned)
        return True
    except ValueError:
        return False


def estimate_confidence(rows: List[List[str]], page) -> float:
    """
    Estimate extraction confidence based on table structure.
    """
    if not rows:
        return 0.0

    # Factors that increase confidence:
    # 1. Consistent column count
    # 2. Low percentage of empty cells
    # 3. Clear text (not garbled)

    col_counts = [len(row) for row in rows]
    consistent_cols = len(set(col_counts)) == 1

    total_cells = sum(len(row) for row in rows)
    non_empty_cells = sum(1 for row in rows for cell in row if cell.strip())
    fill_rate = non_empty_cells / total_cells if total_cells > 0 else 0

    confidence = 0.5  # Base confidence

    if consistent_cols:
        confidence += 0.3

    confidence += fill_rate * 0.2

    return min(1.0, confidence)


def get_table_bbox(page, table_idx: int) -> Optional[List[float]]:
    """
    Get bounding box for table (if available).
    pdfplumber doesn't directly provide this, so we return None.
    """
    # This would require more complex analysis
    # For now, return None
    return None


def main():
    parser = argparse.ArgumentParser(description="Extract tables from PDF using pdfplumber")
    parser.add_argument("--input", "-i", required=True, help="Input PDF file")
    parser.add_argument("--pages", help="Comma-separated page numbers (1-indexed)")
    parser.add_argument("--min-rows", type=int, default=2, help="Minimum rows")
    parser.add_argument("--min-cols", type=int, default=2, help="Minimum columns")
    parser.add_argument("--ocr", action="store_true", help="Enable OCR (requires tesseract)")

    args = parser.parse_args()

    # Parse pages
    pages = None
    if args.pages:
        try:
            pages = [int(p.strip()) for p in args.pages.split(",")]
        except ValueError:
            print(json.dumps({"error": "Invalid page numbers"}), file=sys.stderr)
            sys.exit(1)

    try:
        tables = extract_tables_from_pdf(
            pdf_path=args.input,
            pages=pages,
            min_rows=args.min_rows,
            min_cols=args.min_cols,
            enable_ocr=args.ocr
        )

        # Output JSON to stdout
        print(json.dumps(tables, indent=2))

    except FileNotFoundError:
        print(json.dumps({"error": f"File not found: {args.input}"}), file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(json.dumps({"error": str(e)}), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
