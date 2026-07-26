from pathlib import Path


PAGE_WIDTH = 612
PAGE_HEIGHT = 792
LEFT = 42
RIGHT = 570


def pdf_escape(text: str) -> str:
    return text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")


def add_text(lines, x, y, text, font="F1", size=9.3, color=(0.12, 0.12, 0.12)):
    lines.append(
        f"BT /{font} {size:.2f} Tf {color[0]:.3f} {color[1]:.3f} {color[2]:.3f} rg "
        f"1 0 0 1 {x:.2f} {y:.2f} Tm ({pdf_escape(text)}) Tj ET"
    )


def build_content() -> str:
    lines = []
    y = 748

    add_text(lines, LEFT, y, "App Summary: NinjaTrader ES Strategy Repo", font="F2", size=18, color=(0.10, 0.16, 0.24))
    y -= 17
    add_text(
        lines,
        LEFT,
        y,
        "Based on repo evidence in ESStructureAnchorAVWAP.cs, ESVwapLite.cs, CHANGES.md, and CODE_REVIEW_ESStructureAnchorAVWAP.md",
        font="F1",
        size=8.2,
        color=(0.35, 0.38, 0.42),
    )
    y -= 14
    lines.append(f"q 0.9 0.9 0.9 RG 0.8 w {LEFT} {y:.2f} m {RIGHT} {y:.2f} l S Q")
    y -= 20

    def heading(text):
        nonlocal y
        add_text(lines, LEFT, y, text, font="F2", size=10.8, color=(0.10, 0.16, 0.24))
        y -= 13

    def body(text):
        nonlocal y
        add_text(lines, LEFT, y, text, font="F1", size=9.3)
        y -= 11

    heading("WHAT IT IS")
    body("This repo contains 54 NinjaTrader 8 Strategy scripts for futures trading.")
    body("Its clearest current focus is ESStructureAnchorAVWAP, plus a lighter ESVwapLite variant.")
    y -= 6

    heading("WHO IT'S FOR")
    body("Primary persona explicitly named in repo: Not found in repo.")
    body("Best repo-based fit: ES futures traders and NinjaTrader strategy builders using rule-based VWAP/AVWAP systems.")
    y -= 6

    heading("WHAT IT DOES")
    bullets = [
        "Ships 54 custom strategies spanning AVWAP/VWAP, pivots, opening range, EMA pullbacks, and session-level pullbacks.",
        "Uses ESStructureAnchorAVWAP for structure anchors, regime gates, and risk-first daily controls.",
        "Combines HOD/LOD, structural overrides, impulse-origin anchors, session VWAP, and WTD AVWAP anchors.",
        "Tracks WTD AVWAP from Sunday 17:00 CT with a running accumulator beyond the 256-bar lookback limit.",
        "Applies ATR/time-window/gap-day filters plus caps for opportunities, consecutive losses, daily R, and per-trade risk.",
        "Places bracket orders, moves stops to breakeven at 1R, logs MFE/MAE, and supports chart overlays plus Q/A/C manual hotkeys.",
    ]
    for bullet in bullets:
        body(f"- {bullet}")
    y -= 6

    heading("HOW IT WORKS")
    body("Bars, volume, and trading-hours data feed NinjaTrader strategy lifecycle methods.")
    body("OnStateChange loads ATR, EMA, SMA, ADX, PriorDayOHLC, time-zone handling, and manual AVWAP2 anchors.")
    body("OnBarUpdate resets session state, updates daily extremes and weekly accumulators, then builds active anchors.")
    body("Anchor output flows through regime/risk gates, then into EnterLong/EnterShort with SetStopLoss/SetProfitTarget.")
    body("OnExecutionUpdate records realized R, MFE/MAE, and chart/log telemetry for monitoring.")
    y -= 6

    heading("HOW TO RUN")
    body("1. Use NinjaTrader 8 / NinjaScript; CHANGES.md explicitly notes NinjaTrader 8 on .NET Framework 4.8.")
    body("2. Load a strategy file such as ESStructureAnchorAVWAP.cs or ESVwapLite.cs into NinjaTrader and compile it.")
    body("3. Run it on ES data with at least 50 bars of history; repo review references ES 2-minute bars as the baseline.")
    body("4. Configure anchor/risk parameters and optional chart or log toggles in the strategy properties.")
    body("5. Exact import path, required data connection, and sample workspace/template: Not found in repo.")

    footer_y = 50
    lines.append(f"q 0.9 0.9 0.9 RG 0.8 w {LEFT} {footer_y + 12:.2f} m {RIGHT} {footer_y + 12:.2f} l S Q")
    add_text(
        lines,
        LEFT,
        footer_y,
        "Summary generated from local repo evidence only. No README or end-user setup guide was present in the workspace.",
        font="F1",
        size=8.0,
        color=(0.38, 0.40, 0.44),
    )

    return "\n".join(lines)


def build_pdf_bytes(content_stream: str) -> bytes:
    objects = []

    def add_object(payload: bytes) -> int:
        objects.append(payload)
        return len(objects)

    catalog_id = add_object(b"<< /Type /Catalog /Pages 2 0 R >>")
    assert catalog_id == 1
    pages_id = add_object(b"<< /Type /Pages /Count 1 /Kids [3 0 R] >>")
    assert pages_id == 2
    page_id = add_object(
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> "
        b"/Contents 6 0 R >>"
    )
    assert page_id == 3
    font_regular_id = add_object(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    assert font_regular_id == 4
    font_bold_id = add_object(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")
    assert font_bold_id == 5
    stream_bytes = content_stream.encode("latin-1")
    content_id = add_object(
        b"<< /Length " + str(len(stream_bytes)).encode("ascii") + b" >>\nstream\n" + stream_bytes + b"\nendstream"
    )
    assert content_id == 6

    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for index, payload in enumerate(objects, start=1):
        offsets.append(len(out))
        out.extend(f"{index} 0 obj\n".encode("ascii"))
        out.extend(payload)
        out.extend(b"\nendobj\n")

    xref_pos = len(out)
    out.extend(f"xref\n0 {len(objects) + 1}\n".encode("ascii"))
    out.extend(b"0000000000 65535 f \n")
    for offset in offsets[1:]:
        out.extend(f"{offset:010d} 00000 n \n".encode("ascii"))

    out.extend(
        (
            "trailer\n"
            f"<< /Size {len(objects) + 1} /Root 1 0 R >>\n"
            "startxref\n"
            f"{xref_pos}\n"
            "%%EOF\n"
        ).encode("ascii")
    )
    return bytes(out)


def main():
    output_path = Path("/Users/gautham/Downloads/ninja/output/pdf/ninja_app_summary.pdf")
    content = build_content()
    pdf_bytes = build_pdf_bytes(content)
    output_path.write_bytes(pdf_bytes)
    print(output_path)


if __name__ == "__main__":
    main()
