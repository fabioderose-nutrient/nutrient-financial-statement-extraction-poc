"""Generate a fictitious set of company financial statements for the demo.

The company, the people and every figure are invented. The figures are internally
consistent (every subtotal, total, note and the cash flow reconcile), so the
arithmetic checks of the demo should all pass on this sample.

Usage:
    python generate_financial_statements.py [output.pdf]
"""

import sys
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import (
    KeepTogether,
    PageBreak,
    Paragraph,
    SimpleDocTemplate,
    Spacer,
    Table,
    TableStyle,
)

COMPANY = "Example Components Limited"
YEAR, PRIOR = 2025, 2024
FOOTER = "Fictitious sample generated for a Nutrient demo. The company, the people and the figures are invented."

styles = getSampleStyleSheet()
BODY = ParagraphStyle("body", parent=styles["BodyText"], fontName="Helvetica", fontSize=9.5, leading=13)
SMALL = ParagraphStyle("small", parent=BODY, fontSize=8.5, leading=11)
H1 = ParagraphStyle("h1", parent=styles["Heading1"], fontName="Helvetica-Bold", fontSize=15, spaceAfter=8)
H2 = ParagraphStyle("h2", parent=styles["Heading2"], fontName="Helvetica-Bold", fontSize=11.5, spaceBefore=8, spaceAfter=4)
H3 = ParagraphStyle("h3", parent=BODY, fontName="Helvetica-Bold", fontSize=9.5, spaceBefore=6, spaceAfter=2)
TITLE = ParagraphStyle("title", parent=H1, fontSize=22, leading=28, alignment=TA_CENTER)
CENTER = ParagraphStyle("center", parent=BODY, alignment=TA_CENTER, fontSize=11, leading=16)


def fmt(v):
    """Format an amount as printed in accounts: thousands separators, brackets for negatives, '-' for nil."""
    if v is None:
        return ""
    if isinstance(v, str):
        return v
    if v == 0:
        return "-"
    s = f"{abs(v):,}"
    return f"({s})" if v < 0 else s


def statement(rows, note_col=True, year_labels=(str(YEAR), str(PRIOR)), currency="€"):
    """A primary statement table.

    rows: (label, note, current, prior, style) where style is one of
    '' (line item), 'sub' (subtotal with a rule above), 'total' (double rule),
    'head' (bold caption without figures).
    """
    header = ["", "Notes" if note_col else "", year_labels[0], year_labels[1]]
    data = [header, ["", "", currency, currency]]
    cmds = [
        ("FONT", (0, 0), (-1, 1), "Helvetica-Bold", 9),
        ("FONT", (0, 2), (-1, -1), "Helvetica", 9.5),
        ("ALIGN", (1, 0), (-1, -1), "RIGHT"),
        ("ALIGN", (1, 0), (1, -1), "CENTER"),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 2.5),
        ("TOPPADDING", (0, 0), (-1, -1), 2.5),
    ]
    for label, note, cur, pri, style in rows:
        r = len(data)
        data.append([label, note or "", fmt(cur), fmt(pri)])
        if style == "head":
            cmds.append(("FONT", (0, r), (-1, r), "Helvetica-Bold", 9.5))
        if style in ("sub", "total"):
            cmds.append(("LINEABOVE", (2, r), (3, r), 0.6, colors.black))
            cmds.append(("FONT", (0, r), (-1, r), "Helvetica-Bold", 9.5))
        if style == "total":
            cmds.append(("LINEBELOW", (2, r), (3, r), 0.6, colors.black))
            cmds.append(("LINEBELOW", (2, r), (3, r), 0.3, colors.black, 0, None, None, 2, 2))
    t = Table(data, colWidths=[95 * mm, 16 * mm, 30 * mm, 30 * mm], hAlign="LEFT")
    t.setStyle(TableStyle(cmds))
    return t


def note_table(rows, year_labels=(str(YEAR), str(PRIOR))):
    return statement(rows, note_col=False, year_labels=year_labels)


def matrix(header, rows, widths):
    """A table with several money columns (fixed assets, changes in equity)."""
    data = [header] + [[r[0]] + [fmt(v) for v in r[1:-1]] for r in rows]
    cmds = [
        ("FONT", (0, 0), (-1, 0), "Helvetica-Bold", 8.5),
        ("FONT", (0, 1), (-1, -1), "Helvetica", 9),
        ("ALIGN", (1, 0), (-1, -1), "RIGHT"),
        ("VALIGN", (0, 0), (-1, -1), "BOTTOM"),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 2.5),
        ("TOPPADDING", (0, 0), (-1, -1), 2.5),
    ]
    for i, r in enumerate(rows, start=1):
        style = r[-1]
        if style == "head":
            cmds.append(("FONT", (0, i), (-1, i), "Helvetica-Bold", 9))
        if style in ("sub", "total"):
            cmds.append(("LINEABOVE", (1, i), (-1, i), 0.6, colors.black))
            cmds.append(("FONT", (0, i), (-1, i), "Helvetica-Bold", 9))
    t = Table(data, colWidths=widths, hAlign="LEFT")
    t.setStyle(TableStyle(cmds))
    return t


def p(text, style=BODY):
    return Paragraph(text, style)


def footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 7.5)
    canvas.setFillColor(colors.HexColor("#555555"))
    canvas.drawString(18 * mm, 10 * mm, FOOTER)
    canvas.drawRightString(A4[0] - 18 * mm, 10 * mm, f"Page {doc.page}")
    canvas.restoreState()


def build(path):
    story = []

    # ------------------------------------------------------------------ cover
    story += [
        Spacer(1, 70 * mm),
        p(COMPANY, TITLE),
        Spacer(1, 6 * mm),
        p("Directors' report and financial statements", CENTER),
        p(f"for the financial year ended 31 December {YEAR}", CENTER),
        Spacer(1, 12 * mm),
        p("Registered number: 000000", CENTER),
        Spacer(1, 40 * mm),
        p("FICTITIOUS SAMPLE DOCUMENT", CENTER),
        p("Created for a Nutrient.io financial statement extraction demo.", CENTER),
        PageBreak(),
    ]

    # --------------------------------------------------- company information
    info = [
        ["Directors", "Jane Example\nJohn Sample"],
        ["Company secretary", "Alex Placeholder"],
        ["Registered number", "000000"],
        ["Registered office", "Unit 4, Example Business Park\nSampletown"],
        ["Independent auditor", "Sample & Co.\nChartered Accountants and Statutory Audit Firm"],
        ["Bankers", "Example Bank plc"],
    ]
    t = Table(info, colWidths=[50 * mm, 110 * mm], hAlign="LEFT")
    t.setStyle(TableStyle([("FONT", (0, 0), (0, -1), "Helvetica-Bold", 9.5), ("FONT", (1, 0), (1, -1), "Helvetica", 9.5),
                           ("VALIGN", (0, 0), (-1, -1), "TOP"), ("BOTTOMPADDING", (0, 0), (-1, -1), 6)]))
    contents = [
        ["Company information", "2"], ["Directors' report", "3"], ["Statement of directors' responsibilities", "3"],
        ["Independent auditor's report", "4"], ["Profit and loss account", "5"], ["Balance sheet", "6"],
        ["Statement of changes in equity", "7"], ["Statement of cash flows", "8"],
        ["Notes to the financial statements", "9"],
    ]
    c = Table(contents, colWidths=[120 * mm, 20 * mm], hAlign="LEFT")
    c.setStyle(TableStyle([("FONT", (0, 0), (-1, -1), "Helvetica", 9.5), ("ALIGN", (1, 0), (1, -1), "RIGHT")]))
    story += [p("Company information", H1), t, Spacer(1, 10 * mm), p("Contents", H2), c, PageBreak()]

    # -------------------------------------------------------- directors' report
    story += [
        p("Directors' report", H1),
        p(f"The directors present their report and the audited financial statements for the financial year ended 31 December {YEAR}."),
        p("Principal activities", H2),
        p("The company designs and manufactures precision metal components for the industrial equipment sector."),
        p("Review of the business", H2),
        p(f"Turnover increased by 10.1% to €4,862,350 ({PRIOR}: €4,415,900), driven by new contracts in the United Kingdom "
          "and continental Europe. Gross margin improved to 40.0% as raw material prices stabilised. The directors consider "
          "the results for the year and the financial position at the year end to be satisfactory."),
        p("Results and dividends", H2),
        p(f"The profit for the financial year amounted to €576,625 ({PRIOR}: €447,186). Dividends of €150,000 "
          f"({PRIOR}: €120,000) were paid during the year."),
        p("Going concern", H2),
        p("The directors have prepared cash flow forecasts for a period of at least twelve months from the date of approval "
          "of these financial statements. Based on these forecasts and the available bank facilities, the directors are satisfied "
          "that the company has adequate resources to continue in operational existence for the foreseeable future."),
        p("Events after the end of the financial year", H2),
        p("In February 2026 the company signed a ten-year lease for an additional warehouse. There have been no other "
          "significant events affecting the company since the year end."),
        p("Statement of directors' responsibilities", H2),
        p("The directors are responsible for preparing the directors' report and the financial statements in accordance with "
          "applicable law and regulations, including FRS 102 <i>The Financial Reporting Standard applicable in the UK and "
          "Republic of Ireland</i>. The directors must not approve the financial statements unless they are satisfied that they "
          "give a true and fair view of the assets, liabilities and financial position of the company at the financial year end "
          "and of its profit or loss for the financial year."),
        Spacer(1, 6 * mm),
        p("On behalf of the board"),
        p("Jane Example, Director &nbsp;&nbsp;&nbsp;&nbsp; John Sample, Director"),
        p("24 March 2026"),
        PageBreak(),
    ]

    # ---------------------------------------------------------- auditor report
    story += [
        p(f"Independent auditor's report to the members of {COMPANY}", H1),
        p("Opinion", H2),
        p(f"We have audited the financial statements of {COMPANY} for the financial year ended 31 December {YEAR}, which "
          "comprise the profit and loss account, the balance sheet, the statement of changes in equity, the statement of cash "
          "flows and the related notes, including a summary of significant accounting policies."),
        p(f"In our opinion the financial statements give a true and fair view of the assets, liabilities and financial position "
          f"of the company as at 31 December {YEAR} and of its profit for the year then ended, and have been properly prepared "
          "in accordance with FRS 102."),
        p("Basis for opinion", H2),
        p("We conducted our audit in accordance with International Standards on Auditing. We believe that the audit evidence "
          "we have obtained is sufficient and appropriate to provide a basis for our opinion."),
        p("Conclusions relating to going concern", H2),
        p("In auditing the financial statements, we have concluded that the directors' use of the going concern basis of "
          "accounting in the preparation of the financial statements is appropriate."),
        Spacer(1, 6 * mm),
        p("Sample & Co., Chartered Accountants and Statutory Audit Firm"),
        p("24 March 2026"),
        PageBreak(),
    ]

    # ---------------------------------------------------------- profit and loss
    story += [
        p("Profit and loss account", H1),
        p(f"for the financial year ended 31 December {YEAR}", SMALL),
        Spacer(1, 4 * mm),
        statement([
            ("Turnover", "3", 4862350, 4415900, ""),
            ("Cost of sales", "", -2917410, -2693700, ""),
            ("Gross profit", "", 1944940, 1722200, "sub"),
            ("Administrative expenses", "", -1286520, -1198340, ""),
            ("Other operating income", "", 42800, 38500, ""),
            ("Operating profit", "4", 701220, 562360, "sub"),
            ("Interest receivable and similar income", "", 6150, 3920, ""),
            ("Interest payable and similar charges", "6", -48370, -55210, ""),
            ("Profit before taxation", "", 659000, 511070, "sub"),
            ("Tax on profit", "7", -82375, -63884, ""),
            ("Profit for the financial year", "", 576625, 447186, "total"),
        ]),
        Spacer(1, 4 * mm),
        p("All amounts relate to continuing operations. There was no other comprehensive income for the year.", SMALL),
        PageBreak(),
    ]

    # -------------------------------------------------------------- balance sheet
    story += [
        p("Balance sheet", H1),
        p(f"as at 31 December {YEAR}", SMALL),
        Spacer(1, 4 * mm),
        statement([
            ("Fixed assets", "", None, None, "head"),
            ("Intangible assets", "8", 85000, 102000, ""),
            ("Tangible assets", "9", 1642300, 1518600, ""),
            ("Financial assets", "10", 50000, 50000, ""),
            ("Total fixed assets", "", 1777300, 1670600, "sub"),
            ("Current assets", "", None, None, "head"),
            ("Stocks", "11", 612450, 574300, ""),
            ("Debtors", "12", 893720, 801150, ""),
            ("Cash at bank and in hand", "", 872940, 712450, ""),
            ("Total current assets", "", 2379110, 2087900, "sub"),
            ("Creditors: amounts falling due within one year", "13", -1124560, -1013275, ""),
            ("Net current assets", "", 1254550, 1074625, "sub"),
            ("Total assets less current liabilities", "", 3031850, 2745225, "sub"),
            ("Creditors: amounts falling due after more than one year", "14", -640000, -780000, ""),
            ("Net assets", "", 2391850, 1965225, "total"),
            ("Capital and reserves", "", None, None, "head"),
            ("Called up share capital presented as equity", "15", 100000, 100000, ""),
            ("Profit and loss account", "", 2291850, 1865225, ""),
            ("Shareholders' funds", "", 2391850, 1965225, "total"),
        ]),
        Spacer(1, 6 * mm),
        p("The financial statements were approved and authorised for issue by the board of directors on 24 March 2026 "
          "and signed on its behalf by:"),
        p("Jane Example, Director &nbsp;&nbsp;&nbsp;&nbsp; John Sample, Director"),
        PageBreak(),
    ]

    # ----------------------------------------------------- changes in equity
    w = [78 * mm, 30 * mm, 32 * mm, 30 * mm]
    head = ["", "Called up share\ncapital\n€", "Profit and loss\naccount\n€", "Total\n€"]
    story += [
        p("Statement of changes in equity", H1),
        p(f"for the financial year ended 31 December {YEAR}", SMALL),
        Spacer(1, 4 * mm),
        matrix(head, [
            (f"At 1 January {PRIOR}", 100000, 1538039, 1638039, "sub"),
            ("Profit for the financial year", 0, 447186, 447186, ""),
            ("Dividends paid", 0, -120000, -120000, ""),
            (f"At 31 December {PRIOR}", 100000, 1865225, 1965225, "total"),
        ], w),
        Spacer(1, 8 * mm),
        matrix(head, [
            (f"At 1 January {YEAR}", 100000, 1865225, 1965225, "sub"),
            ("Profit for the financial year", 0, 576625, 576625, ""),
            ("Dividends paid", 0, -150000, -150000, ""),
            (f"At 31 December {YEAR}", 100000, 2291850, 2391850, "total"),
        ], w),
        PageBreak(),
    ]

    # ------------------------------------------------------------- cash flows
    story += [
        p("Statement of cash flows", H1),
        p(f"for the financial year ended 31 December {YEAR}", SMALL),
        Spacer(1, 4 * mm),
        statement([
            ("Cash flows from operating activities", "", None, None, "head"),
            ("Profit for the financial year", "", 576625, 447186, ""),
            ("Depreciation of tangible assets", "9", 186400, 172900, ""),
            ("Amortisation of intangible assets", "8", 17000, 17000, ""),
            ("Interest payable and similar charges", "6", 48370, 55210, ""),
            ("Interest receivable and similar income", "", -6150, -3920, ""),
            ("Tax on profit", "7", 82375, 63884, ""),
            ("Increase in stocks", "", -38150, -32500, ""),
            ("Increase in debtors", "", -92570, -44750, ""),
            ("Increase in creditors", "", 99050, 155044, ""),
            ("Cash generated from operations", "", 872950, 830054, "sub"),
            ("Interest paid", "", -48370, -55210, ""),
            ("Interest received", "", 6150, 3920, ""),
            ("Tax paid", "", -70140, -59544, ""),
            ("Net cash from operating activities", "", 760590, 719220, "sub"),
            ("Cash flows from investing activities", "", None, None, "head"),
            ("Purchase of tangible assets", "9", -310100, -245500, ""),
            ("Net cash used in investing activities", "", -310100, -245500, "sub"),
            ("Cash flows from financing activities", "", None, None, "head"),
            ("Repayment of bank loans", "", -140000, -140000, ""),
            ("Dividends paid", "16", -150000, -120000, ""),
            ("Net cash used in financing activities", "", -290000, -260000, "sub"),
            ("Net increase in cash and cash equivalents", "", 160490, 213720, "sub"),
            ("Cash and cash equivalents at 1 January", "", 712450, 498730, ""),
            ("Cash and cash equivalents at 31 December", "", 872940, 712450, "total"),
        ]),
        PageBreak(),
    ]

    # ------------------------------------------------------------------ notes
    story += [
        p("Notes to the financial statements", H1),
        p("1. General information", H3),
        p(f"{COMPANY} is a private company limited by shares. Its registered office is Unit 4, Example Business Park, "
          "Sampletown. The financial statements are presented in euro (€), the functional currency of the company, in full units."),
        p("2. Accounting policies", H3),
        p("<b>Basis of preparation.</b> The financial statements have been prepared on the going concern basis, under the "
          "historical cost convention, in accordance with FRS 102 <i>The Financial Reporting Standard applicable in the UK "
          "and Republic of Ireland</i>."),
        p("<b>Turnover</b> comprises the invoiced value of goods supplied, excluding value added tax and trade discounts."),
        p("<b>Tangible fixed assets</b> are stated at cost less accumulated depreciation. Depreciation is provided on a "
          "straight-line basis: plant and machinery 12.5% per annum, fixtures and fittings 20% per annum."),
        p("<b>Intangible assets</b> (capitalised development costs) are amortised over ten years."),
        p("<b>Stocks</b> are stated at the lower of cost and net realisable value."),
        p("<b>Taxation.</b> Current tax is provided at amounts expected to be paid using the tax rates enacted at the balance sheet date."),
        KeepTogether([p("3. Turnover", H3), p("Analysis of turnover by geographical market:", SMALL), note_table([
            ("Ireland", "", 2820150, 2649540, ""),
            ("United Kingdom", "", 1312800, 1148130, ""),
            ("Rest of Europe", "", 729400, 618230, ""),
            ("Total", "", 4862350, 4415900, "total"),
        ])]),
        KeepTogether([p("4. Operating profit", H3), p("Operating profit is stated after charging:", SMALL), note_table([
            ("Depreciation of tangible assets", "", 186400, 172900, ""),
            ("Amortisation of intangible assets", "", 17000, 17000, ""),
            ("Auditor's remuneration - audit of the financial statements", "", 18500, 17200, ""),
            ("Professional fees", "", 26300, 24100, ""),
        ])]),
        KeepTogether([p("5. Employees and directors", H3), p("The average monthly number of persons employed by the company during the year was:", SMALL),
            matrix(
                ["", f"{YEAR}\nNumber", f"{PRIOR}\nNumber"],
                [("Production", 38, 36, ""), ("Administration", 14, 13, ""), ("Total", 52, 49, "total")],
                [95 * mm, 38 * mm, 38 * mm]),
            Spacer(1, 3 * mm), p("Staff costs:", SMALL), note_table([
                ("Wages and salaries", "", 2145600, 1987300, ""),
                ("Social insurance costs", "", 236000, 218600, ""),
                ("Pension costs", "", 64400, 59600, ""),
                ("Total staff costs", "", 2446000, 2265500, "total"),
            ]),
            Spacer(1, 3 * mm), p("Directors' remuneration:", SMALL), note_table([
                ("Emoluments for qualifying services", "", 248000, 236000, ""),
                ("Pension contributions", "", 18000, 18000, ""),
                ("Total directors' remuneration", "", 266000, 254000, "total"),
            ])]),
        KeepTogether([p("6. Interest payable and similar charges", H3), note_table([
            ("Interest on bank loans", "", 48370, 55210, ""),
        ])]),
        KeepTogether([p("7. Tax on profit", H3), note_table([
            ("Current tax: corporation tax on profit for the year", "", 82375, 63884, ""),
            ("Tax on profit", "", 82375, 63884, "total"),
        ]), Spacer(1, 3 * mm), p("Reconciliation of the tax charge:", SMALL), note_table([
            ("Profit before taxation", "", 659000, 511070, ""),
            ("Profit before tax at the standard rate of corporation tax (12.5%)", "", 82375, 63884, ""),
            ("Expenses not deductible for tax purposes", "", 1240, 980, ""),
            ("Capital allowances in excess of depreciation", "", -1240, -980, ""),
            ("Total tax charge for the year", "", 82375, 63884, "total"),
        ])]),
        KeepTogether([p("8. Intangible assets", H3), p("Development costs", SMALL), note_table([
            ("Cost", "", None, None, "head"),
            ("At 1 January", "", 170000, 170000, "sub"),
            ("Additions", "", 0, 0, ""),
            ("At 31 December", "", 170000, 170000, "total"),
            ("Amortisation", "", None, None, "head"),
            ("At 1 January", "", 68000, 51000, "sub"),
            ("Charge for the year", "", 17000, 17000, ""),
            ("At 31 December", "", 85000, 68000, "total"),
            ("Net book value at 31 December", "", 85000, 102000, ""),
        ])]),
        KeepTogether([p("9. Tangible assets", H3), matrix(
            ["", "Plant and\nmachinery\n€", "Fixtures and\nfittings\n€", "Total\n€"],
            [
                ("Cost", None, None, None, "head"),
                (f"At 1 January {YEAR}", 2310000, 535000, 2845000, "sub"),
                ("Additions", 268600, 41500, 310100, ""),
                (f"At 31 December {YEAR}", 2578600, 576500, 3155100, "total"),
                ("Depreciation", None, None, None, "head"),
                (f"At 1 January {YEAR}", 1042300, 284100, 1326400, "sub"),
                ("Charge for the year", 151200, 35200, 186400, ""),
                (f"At 31 December {YEAR}", 1193500, 319300, 1512800, "total"),
                ("Net book value", None, None, None, "head"),
                (f"At 31 December {YEAR}", 1385100, 257200, 1642300, ""),
                (f"At 31 December {PRIOR}", 1267700, 250900, 1518600, ""),
            ],
            [78 * mm, 30 * mm, 32 * mm, 30 * mm])]),
        KeepTogether([p("10. Financial assets", H3), note_table([
            ("Investment in unlisted shares, at cost", "", 50000, 50000, ""),
        ])]),
        KeepTogether([p("11. Stocks", H3), note_table([
            ("Raw materials and consumables", "", 248300, 231900, ""),
            ("Work in progress", "", 96150, 88400, ""),
            ("Finished goods", "", 268000, 254000, ""),
            ("Total stocks", "", 612450, 574300, "total"),
        ])]),
        KeepTogether([p("12. Debtors", H3), note_table([
            ("Trade debtors", "", 812420, 728650, ""),
            ("Prepayments and accrued income", "", 81300, 72500, ""),
            ("Total debtors", "", 893720, 801150, "total"),
        ])]),
        KeepTogether([p("13. Creditors: amounts falling due within one year", H3), note_table([
            ("Bank loans", "", 140000, 140000, ""),
            ("Trade creditors", "", 612340, 548910, ""),
            ("Corporation tax", "", 44175, 31940, ""),
            ("Other taxation and social insurance", "", 96420, 88605, ""),
            ("Accruals and deferred income", "", 231625, 203820, ""),
            ("Total", "", 1124560, 1013275, "total"),
        ])]),
        KeepTogether([p("14. Creditors: amounts falling due after more than one year", H3), note_table([
            ("Bank loans", "", 640000, 780000, ""),
        ]), Spacer(1, 3 * mm), p("Maturity of bank loans falling due after more than one year:", SMALL), note_table([
            ("Between one and two years", "", 140000, 140000, ""),
            ("Between two and five years", "", 420000, 420000, ""),
            ("After more than five years", "", 80000, 220000, ""),
            ("Total", "", 640000, 780000, "total"),
        ]), p("The bank loans are secured by a fixed charge over the company's plant and machinery.", SMALL)]),
        KeepTogether([p("15. Called up share capital", H3), note_table([
            ("Allotted, called up and fully paid: 100,000 ordinary shares of €1 each", "", 100000, 100000, ""),
        ])]),
        KeepTogether([p("16. Dividends", H3), note_table([
            (f"Dividends paid on ordinary shares (€1.50 per share; {PRIOR}: €1.20 per share)", "", 150000, 120000, ""),
        ])]),
        p("17. Contingent liabilities and capital commitments", H3),
        p("There were no contingent liabilities at 31 December 2025 (2024: none). Capital expenditure contracted for but not "
          "provided for in the financial statements amounted to €75,000 (2024: nil)."),
        p("18. Related party transactions", H3),
        p("The compensation of key management personnel, who are the directors, is disclosed in note 5. There were no other "
          "transactions with related parties during the year."),
        p("19. Events after the end of the financial year", H3),
        p("In February 2026 the company signed a ten-year lease for an additional warehouse with an annual rent of €48,000. "
          "This is a non-adjusting event."),
        p("20. Approval of the financial statements", H3),
        p("The financial statements were approved by the board of directors on 24 March 2026."),
    ]

    doc = SimpleDocTemplate(
        str(path), pagesize=A4, leftMargin=18 * mm, rightMargin=18 * mm, topMargin=18 * mm, bottomMargin=18 * mm,
        title=f"{COMPANY} - Financial statements {YEAR} (fictitious sample)", author="Nutrient demo sample generator",
        subject="Fictitious financial statements", creator="generate_financial_statements.py",
    )
    doc.build(story, onFirstPage=footer, onLaterPages=footer)


if __name__ == "__main__":
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).with_name("example-components-financial-statements-2025.pdf")
    build(out)
    print(f"Wrote {out} ({out.stat().st_size // 1024} KB)")
