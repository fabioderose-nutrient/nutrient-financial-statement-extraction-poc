using XTractFlow.API.Document;
using XTractFlow.API.Document.Fields;

namespace FinancialExtraction.Api.Templates;

public static class ExtractionTemplates
{
    public const string FinancialStatements = "Financial Statements";
    public const string FormB1 = "Form B1";
    public const string FormB1C = "Form B1C";
    public const string FormB7 = "Form B7";
    public const string FormB10 = "Form B10";

    public static List<DocumentTemplate> BuildAll(FieldSpecFile spec)
        => new() { BuildFinancialStatements(spec), BuildB1(), BuildB1C(), BuildB7(), BuildB10() };

    private static TemplateField Field(string name, string description, FieldDataFormat format)
        => new() { Name = name, SemanticDescription = description, Format = format };

    private static TemplateField F(string name, string description, FieldDataFormat format = FieldDataFormat.Text)
        => new() { Name = name, SemanticDescription = description, Format = format };

    public const string PriorSuffix = "__prior";

    public static DocumentTemplate BuildFinancialStatements(
        FieldSpecFile spec, IEnumerable<FieldSpec>? fields = null, bool includeCommentary = true,
        bool includeMeta = true)
    {
        var t = new DocumentTemplate
        {
            Name = FinancialStatements,
            Identifier = "financial_statements",
            SemanticDescription =
                "Irish company statutory financial statements / annual accounts (FRS 105, FRS 102, " +
                "FRS 102 Section 1A, or IFRS), including a profit and loss account / income statement, " +
                "balance sheet / statement of financial position, and accompanying notes.",
        };

        if (includeMeta)
        {
            t.AddField(Field("presentation_scale",
                "The scale the monetary figures are presented in, e.g. €'000 (thousands), €m (millions), " +
                "or full units. Return the exact caption shown near the column headings or in the accounting policies.",
                FieldDataFormat.Text));
            t.AddField(Field("reporting_framework",
                "The financial reporting framework/standard the accounts are prepared under. Return the " +
                "exact standard named in the accounting-policies note or basis-of-preparation statement, " +
                "e.g. 'FRS 105', 'FRS 102 Section 1A', 'FRS 102', 'FRS 101', 'FRS 100', or 'IFRS'.",
                FieldDataFormat.Text));
        }

        foreach (var f in (fields ?? spec.AllExtractable()))
        {
            bool paired = f.IsMonetary || f.Key is "average_number_of_persons_employed" or "financial_year_end_date";
            var format = f.IsMonetary ? FieldDataFormat.Text
                : f.Key == "average_number_of_persons_employed" ? FieldDataFormat.Number
                : f.Key == "financial_year_end_date" ? FieldDataFormat.Date
                : FieldDataFormat.Text;

            if (!paired)
            {
                t.AddField(Field(f.Key, f.SemanticDescription(), format));
                continue;
            }
            t.AddField(Field(f.Key,
                f.SemanticDescription() + " Take the CURRENT (latest) reporting year column.", format));
            t.AddField(Field(f.Key + PriorSuffix,
                f.SemanticDescription() + " Take the PRIOR year (comparative) column.", format));
        }

        if (includeCommentary)
        {
            foreach (var (key, label, cue) in Commentaries)
            {
                t.AddField(Field(key, $"The verbatim text of the {label} ({cue}).", FieldDataFormat.Text));
                t.AddField(Field(key + SentimentSuffix,
                    $"Classify the overall sentiment of the {label} as exactly one of: positive, negative, neutral.",
                    FieldDataFormat.Text));
            }

            t.AddField(ArrayField("notes",
                "Every MATERIAL note or remark that carries information a reviewer would care about - " +
                "going concern, contingent liabilities, provisions, post-balance-sheet / subsequent events, " +
                "related-party transactions, significant judgements or estimates, impairments, an auditor's " +
                "emphasis-of-matter, qualification or disclaimer, and any unusual directors' or auditors' " +
                "remark or statement. EXCLUDE routine boilerplate accounting policies. Capture each material " +
                "note as one array item (at most ~10 items).",
                F("title", "A short title identifying the note/remark (e.g. 'Going concern', 'Contingent liabilities', 'Subsequent events')."),
                F("type", "The kind of note: going_concern, contingency, provision, post_balance_sheet_event, related_party, significant_judgement, impairment, auditor_remark, director_remark, other."),
                F("text", "The text of the note / remark (verbatim or lightly trimmed)."),
                F("sentiment", "The sentiment of the note as exactly one of: positive, negative, neutral.")));
        }
        return t;
    }

    public const string TableValidation = "Table Validation";
    public const string TableRowsField = "table_rows";

    public const string ScopeStatements = "statements";
    public const string ScopeNotes = "notes";

    public static string TableRowsFieldFor(string scope) => $"{TableRowsField}_{scope}";

    // Every field name the batches write into, for merging on the way back.
    public static IEnumerable<string> AllTableRowFields()
    {
        yield return TableRowsFieldFor(ScopeStatements);
        yield return TableRowsFieldFor(ScopeNotes);
    }

    public static DocumentTemplate BuildTableValidation(string scope)
    {
        bool statements = scope == ScopeStatements;

        var t = new DocumentTemplate
        {
            Name = $"{TableValidation} ({scope})",
            Identifier = $"table_validation_{scope}",
            SemanticDescription =
                "Irish company statutory financial statements, read for the purpose of listing the rows " +
                "of every table of figures so that their arithmetic can be checked.",
        };

        var whatToReturn = statements
            ? "ONLY the PRIMARY STATEMENTS of this document, and nothing from the numbered notes. " +
              "The primary statements are the statement of comprehensive income (or profit and loss " +
              "account or income statement), the statement of financial position (or balance sheet), " +
              "the statement of changes in equity, and the statement of cash flows if there is one. " +
              "Return every row of each of them. They usually sit on their own page, just before the " +
              "notes begin."
            : "ONLY the tables that sit INSIDE the numbered notes to the financial statements, and " +
              "nothing from the primary statements themselves. Be exhaustive and go through the notes " +
              "one by one from the first to the last, leaving none out: an analysis of revenue by " +
              "location or by category, other operating income, operating profit disclosures, other " +
              "expenses, auditors' remuneration, employee and directors' information, staff numbers and " +
              "costs, finance income and costs, the tax charge and the tax reconciliation, fixed-asset " +
              "and intangible-asset movement tables, financial assets, stocks, debtors, cash, creditors, " +
              "lease liabilities, provisions, deferred tax, share capital, reserves, share based " +
              "payments, commitments and related-party tables. If a note shows labels on the left and " +
              "one or two columns of figures on the right, it is a table and it must be returned. " +
              "Leaving a note table out is much worse than including a doubtful one.";

        t.AddField(ArrayField(TableRowsFieldFor(scope),
            whatToReturn + " " +
            "Return ONE ARRAY ITEM PER ROW, and return the rows in the SAME ORDER as they are printed " +
            "on the page, top to bottom. The order matters, because each subtotal is checked against " +
            "the rows printed above it. " +
            "Only skip purely narrative paragraphs that have no column of figures. " +
            "Split into SEPARATE tables, each with its own table_name, whenever a block restarts its " +
            "own addition rather than continuing the previous one. When one numbered note holds " +
            "several little tables under their own sub-headings, split them (use " +
            "'19. Lease liability - maturity' and '19. Lease liability - amounts recognised in profit " +
            "or loss'). " +
            "The statement of changes in equity almost always shows the CURRENT year and the PRIOR year " +
            "as two stacked blocks, each opening with its own 'Balance at 1 January' (or 'At 1 January') " +
            "line and closing with its own 'Balance at 31 December'. Return each year's block as its OWN " +
            "table (for example 'Statement of Changes in Equity - 2024' and " +
            "'Statement of Changes in Equity - 2023'); never let the second year's opening balance " +
            "continue the first year's running total. " +
            "The balance sheet in particular must be split section by section, because each " +
            "of its sections adds up its own items: return " +
            "'Statement of Financial Position - Fixed assets' (the non-current assets and their total), " +
            "'Statement of Financial Position - Current assets' (the current-asset lines, their total, " +
            "the creditors-due-within-one-year line and the net current assets subtotal) and " +
            "'Statement of Financial Position - Capital and reserves' (the equity lines and their total) " +
            "as three separate tables. " +
            "CRUCIAL for the balance sheet: the lines 'Total assets less current liabilities' and " +
            "'Net assets' (and any 'Total assets' line) are NOT the sum of the rows printed just above " +
            "them; they combine totals from different sections far apart on the page. You MUST give " +
            "each of those lines row_type 'memo' so it is never checked as a running sum. Put them, as " +
            "memo rows, at the end of the 'Current assets' table. Never mark 'Net assets' or 'Total " +
            "assets less current liabilities' as a subtotal or a total. " +
            "Never mix rows belonging to two different additions under one table_name. " +
            "A fixed-asset or intangible-asset movement note (for example '13. Tangible fixed assets') " +
            "always holds THREE stacked sub-tables that must be split: a 'Cost or valuation' section, a " +
            "'Depreciation' (or 'Amortisation') section, and a 'Net book value' section. Each of the " +
            "first two starts from its OWN 'At 1 January' opening balance and ends at its own 'At 31 " +
            "December' total, so return them as separate tables ('13. Tangible fixed assets - cost' and " +
            "'13. Tangible fixed assets - depreciation'). The 'Net book value' figures are cost minus " +
            "depreciation, not a sum of rows, so give them row_type 'memo'. NEVER put a depreciation " +
            "opening balance in the same table as the cost figures. " +
            "GENERAL RULE for any movement or reconciliation (fixed assets, intangibles, provisions, " +
            "equity, reserves): whenever a block RESTARTS from its own 'At 1 January' / 'Balance at 1 " +
            "January' / 'Opening' line, a new table begins. If the same note prints one such block for " +
            "the current year and a second block for the prior year, they are TWO tables, one per year, " +
            "never one. An opening balance is never the continuation of the total printed just above it. " +
            "IMPORTANT, tables with several money columns: there are only two value slots per row, one " +
            "for the current year and one for the prior year. Some tables instead spread across several " +
            "CATEGORY columns, for example a fixed-asset movement table with a column per asset class, " +
            "or a statement of changes in equity with a column for share capital, one for the share " +
            "premium and one for retained earnings. For those, take the figures from the TOTAL column " +
            "only and ignore the individual category columns. NEVER glue several columns together into " +
            "one value: a value must always be a single number as printed in one cell. If such a table " +
            "has no total column at all, leave the table out entirely rather than returning mixed-up " +
            "figures.",
            F("table_name",
                "The exact caption of the table this row belongs to, including its note number when there " +
                "is one, e.g. '18. Current liabilities: amounts falling due within 1 year'. EVERY row of " +
                "the same table must repeat this value character for character, otherwise the rows cannot " +
                "be grouped back together."),
            F("row_label",
                "The label of this row exactly as printed, e.g. 'Trade creditors', 'Payroll taxes', " +
                "'Accruals'. For an unlabelled total row return 'Total'."),
            F("row_type",
                "Exactly one of these four values, and it decides how the row is arithmetically checked. " +
                "'component': an individual line item that is added into the running figure, such as " +
                "'Revenue', 'Administrative expenses', 'Trade creditors', 'Additions' or 'Dividends paid'. " +
                "'subtotal': a line that states a running result reached at that point in the table, " +
                "such as 'Gross profit', 'Operating loss', 'Loss before taxation', 'Total fixed assets', " +
                "'Net current assets' or 'Net assets'. Also use 'subtotal' for an OPENING balance that " +
                "the movements below it start from, such as 'At 1 January 2023' or 'Cost at 1 January'. " +
                "'total': the final result of the table, normally the last row, under a single or double " +
                "rule, such as 'Total comprehensive income for the year', 'At 31 December 2023' or a row " +
                "simply labelled 'Total'. " +
                "'memo': a reference figure printed inside the table that takes no part in the addition, " +
                "typically a line of a different nature from the ones being added. Use 'memo' in " +
                "particular for a line whose figure is DERIVED FROM SOMEWHERE ELSE in the document " +
                "rather than from the rows printed directly above it: for instance " +
                "'Total assets less current liabilities' on a balance sheet, which combines the fixed " +
                "assets section with the current assets section, or a 'Net book value' line, which is " +
                "the cost table minus the depreciation table. Marking those as a subtotal would compare " +
                "them against rows they were never the sum of. " +
                "How the check works, so that you can choose correctly: every 'subtotal' and 'total' is " +
                "compared against the previous subtotal PLUS the components printed between them. So in " +
                "a profit and loss account, revenue and the expense lines are 'component', 'Operating " +
                "loss' is a 'subtotal', finance costs is a 'component' again, 'Loss before taxation' is " +
                "another 'subtotal', and the bottom line is the 'total'. Never mark a genuine line item " +
                "as a subtotal, and never mark a stated running result as a component. " +
                "The tax reconciliation note is the case to get right: the profit or loss BEFORE TAX " +
                "printed at the very top is a 'memo', but the very next line, the one that multiplies " +
                "it by the standard rate of tax, IS the starting 'component' of the addition, and every " +
                "adjustment listed under it is a 'component' too, so that they all add up to the total " +
                "tax charge. Only ever mark one line as 'memo' there."),
            F("current_year_value",
                "The amount in the CURRENT (latest) year column for this row, copied character for " +
                "character as printed, keeping the commas, the decimals and any dash that means nil. " +
                "Copy the brackets ONLY when the document really prints brackets around that number: " +
                "brackets mean a negative amount, so adding a pair that is not on the page silently " +
                "flips the sign. Leave empty if the row has no value in that column."),
            F("prior_year_value",
                "The amount in the PRIOR year (comparative) column for this row, copied character for " +
                "character as printed, keeping the commas, the decimals and any dash that means nil. " +
                "Copy the brackets ONLY when the document really prints brackets around that number: " +
                "brackets mean a negative amount, so adding a pair that is not on the page silently " +
                "flips the sign. Leave empty if the row has no value in that column."),
            F("page_number",
                "The page number of the document on which this table is printed, if it can be determined " +
                "from the page footer or header. Leave empty if unsure."),
            F("currency",
                "The currency of the figures in THIS table, read ONLY from the table itself: the " +
                "currency symbol or code printed in its column headings (for example the '€' or " +
                "'$' or '£' shown under the year, or a caption like 'US$'000' or 'EUR'). Do NOT take " +
                "it from narrative text such as the auditor's opinion or the directors' report. Return " +
                "the ISO code (EUR, USD, GBP) or the symbol exactly as shown. A single set of accounts " +
                "can present different tables in different currencies, so answer for THIS table only and " +
                "repeat the same value on every one of its rows. Leave empty if the table shows no " +
                "currency marker at all."),
            F("scale",
                "The scale that THIS table's money columns are printed in, read from the caption at the " +
                "top of the columns, exactly as shown: for example \"€'000\", \"$'000\", \"£m\", " +
                "\"in thousands\", \"in millions\", or \"figures in €'000\". Leave EMPTY when the " +
                "figures are in full units (a bare '€' with no '000 or 'm' is full units). A " +
                "document can mix scales between tables, so answer for THIS table only and repeat the " +
                "same value on every one of its rows.")));

        return t;
    }

    public const string SentimentSuffix = "__sentiment";

    public static readonly (string Key, string Label, string Cue)[] Commentaries =
    {
        ("directors_declaration", "directors' declaration / responsibilities statement",
            "statement of directors' responsibilities, confirmation of true and fair view"),
        ("auditors_opinion", "auditor's or accountant's opinion",
            "independent auditor's report, 'in our opinion', basis for opinion"),
        ("going_concern", "going concern statement",
            "ability to continue in operational existence for the foreseeable future"),
        ("directors_report", "directors' report business review",
            "principal activities, review of the business, trading performance"),
    };

    public static DocumentTemplate BuildB1()
    {
        var t = FormShell(FormB1, "form_b1",
            "Irish CRO Form B1 - (first) Annual Return of a company, listing company particulars, " +
            "ALL directors, the secretary, past officers and ALL members / shareholdings.");
        AddCompanyParticulars(t);
        AddShareCapital(t);
        AddAnnualReturnDetails(t);
        AddOfficerAndMemberArrays(t);
        AddOtherInformation(t);
        return t;
    }

    public static DocumentTemplate BuildB1C()
    {
        var t = FormShell(FormB1C, "form_b1c",
            "Irish CRO Form B1C - Annual Return General, with company particulars, ALL directors, " +
            "the secretary, past officers and ALL members / shareholdings.");
        AddCompanyParticulars(t);
        AddShareCapital(t);
        AddAnnualReturnDetails(t);
        AddOfficerAndMemberArrays(t);
        AddOtherInformation(t);
        return t;
    }

    public static DocumentTemplate BuildB7()
    {
        var t = FormShell(FormB7, "form_b7",
            "Irish CRO Form B7 - Notice of consolidation, division, conversion, sub-division, " +
            "redenomination or renominalisation of share capital.");
        AddCompanyParticulars(t);
        t.AddField(Field("nature_of_change", "The nature of the share-capital change (consolidation, division, conversion, sub-division, redenomination, renominalisation).", FieldDataFormat.Text));
        t.AddField(Field("effective_date", "The effective date of the change.", FieldDataFormat.Date));
        t.AddField(Field("resolution_date", "The date of the resolution authorising the change, if shown.", FieldDataFormat.Date));
        t.AddField(Field("authorised_capital_before", "Total authorised share capital before the change.", FieldDataFormat.Text));
        t.AddField(Field("authorised_capital_after", "Total authorised share capital after the change.", FieldDataFormat.Text));
        t.AddField(ArrayField("share_capital_changes",
            "Every share-capital change line in the notice (one array item per share class affected).",
            F("share_class", "The class of shares affected."),
            F("nominal_value_before", "Nominal value per share before the change."),
            F("nominal_value_after", "Nominal value per share after the change."),
            F("number_of_shares_before", "Number of shares before the change.", FieldDataFormat.Number),
            F("number_of_shares_after", "Number of shares after the change.", FieldDataFormat.Number)));
        AddSignatories(t);
        AddOtherInformation(t);
        return t;
    }

    public static DocumentTemplate BuildB10()
    {
        var t = FormShell(FormB10, "form_b10",
            "Irish CRO Form B10 - Notice of change of director or secretary, or of their particulars.");
        AddCompanyParticulars(t);
        t.AddField(ArrayField("officer_changes",
            "Every director/secretary change notified in this form (one array item per officer). Capture ALL changes.",
            F("change_type", "The type of change: appointment, resignation, or change of particulars."),
            F("officer_role", "Whether the person is a director or secretary."),
            F("name", "The full name of the officer."),
            F("former_name", "Any former name of the officer, if shown."),
            F("residential_address", "The residential or registered address of the officer."),
            F("date_of_birth", "The officer's date of birth.", FieldDataFormat.Date),
            F("nationality", "The officer's nationality."),
            F("occupation", "The officer's occupation."),
            F("is_body_corporate", "Whether the officer is a body corporate (true/false)."),
            F("corporate_name", "If a body corporate, its company name."),
            F("corporate_number", "If a body corporate, its company registration number."),
            F("other_directorships", "Any other directorships listed for this person."),
            F("effective_date", "The effective date of the change.", FieldDataFormat.Date)));
        AddSignatories(t);
        AddOtherInformation(t);
        return t;
    }

    private static DocumentTemplate FormShell(string name, string id, string description)
        => new() { Name = name, Identifier = id, SemanticDescription = description };

    private static TemplateField ArrayField(string name, string desc, params TemplateField[] items)
        => new() { Name = name, SemanticDescription = desc, Format = FieldDataFormat.Array, ArrayItemFields = items.ToList() };

    private static void AddCompanyParticulars(DocumentTemplate t)
    {
        t.AddField(Field("company_name", "The registered name of the company on the first page.", FieldDataFormat.Text));
        t.AddField(Field("company_number", "The CRO company registration number.", FieldDataFormat.Text));
        t.AddField(Field("company_type", "The company type, e.g. 'Private company limited by shares', 'Designated activity company', 'Company limited by guarantee'.", FieldDataFormat.Text));
        t.AddField(Field("registered_office", "The registered office address of the company.", FieldDataFormat.Text));
        t.AddField(Field("company_email", "The company's email address / contact email, if shown.", FieldDataFormat.Text));
        t.AddField(Field("return_date", "The annual return date / made-up-to date of the filing.", FieldDataFormat.Date));
        t.AddField(Field("financial_year_end", "The financial year end date referenced on the return, if any.", FieldDataFormat.Date));
        t.AddField(Field("principal_activity", "The principal activity / NACE code of the company, if stated.", FieldDataFormat.Text));
        t.AddField(Field("size_exemption", "Any company-size category or exemption claimed on the return that indicates the company's size - e.g. 'small company', 'micro company', 'audit exemption (small company)', 'exemption from filing certain information as a small/micro company'. Return the exact wording found.", FieldDataFormat.Text));
    }

    private static void AddShareCapital(DocumentTemplate t)
    {
        t.AddField(Field("authorised_share_capital", "The total authorised/nominal share capital of the company.", FieldDataFormat.Text));
        t.AddField(Field("issued_share_capital", "The total issued/paid-up share capital of the company.", FieldDataFormat.Text));
    }

    private static void AddAnnualReturnDetails(DocumentTemplate t)
    {
        t.AddField(Field("place_of_business", "The place of business, if different from the registered office.", FieldDataFormat.Text));
        t.AddField(Field("financial_statements_annexed", "Whether financial statements / accounts are annexed to this return (yes/no), if indicated.", FieldDataFormat.Text));
        t.AddField(Field("audit_exemption_claimed", "Whether an audit exemption is claimed and of which type (e.g. small company / dormant company audit exemption).", FieldDataFormat.Text));
        t.AddField(Field("nace_code", "The NACE code / business activity classification, if shown.", FieldDataFormat.Text));
        t.AddField(Field("register_of_beneficial_owners", "Any reference to the register of beneficial owners (RBO), if shown.", FieldDataFormat.Text));
    }

    private static void AddSignatories(DocumentTemplate t)
    {
        t.AddField(ArrayField("signatories",
            "Every person who signed / certified the form (directors, secretary, presenter/filing agent), one array item each.",
            F("name", "The signatory's name."),
            F("capacity", "In what capacity they signed (director, secretary, presenter, agent)."),
            F("date", "The date of signing, if shown.", FieldDataFormat.Date)));
        t.AddField(Field("presenter_details", "The presenter / filing agent name and contact details, if shown.", FieldDataFormat.Text));
    }

    private static void AddOtherInformation(DocumentTemplate t)
    {
        t.AddField(ArrayField("other_information",
            "Capture EVERY other labelled piece of information present ANYWHERE in the document that is " +
            "not already covered by the fields above - be exhaustive, leave nothing out. Examples: reference/" +
            "submission numbers, dates, checkboxes ticked, statements/declarations, place of registration, " +
            "currency, contact details, CRO stamp details, page-level notes, any field:value pair. One array " +
            "item per piece of information.",
            F("label", "The label / name of the information as shown in the document."),
            F("value", "The corresponding value or text.")));
    }

    private static void AddOfficerAndMemberArrays(DocumentTemplate t)
    {
        t.AddField(ArrayField("directors",
            "The FULL list of ALL CURRENT directors of the company. Return every director listed, one array item each - do not stop at the first.",
            F("name", "The director's full name."),
            F("residential_address", "The director's residential address."),
            F("nationality", "The director's nationality."),
            F("occupation", "The director's occupation / business occupation."),
            F("date_of_birth", "The director's date of birth.", FieldDataFormat.Date),
            F("date_of_appointment", "The director's date of appointment, if shown.", FieldDataFormat.Date),
            F("other_directorships", "Any other current directorships listed for this person, if shown."),
            F("email", "The director's email address, if shown.")));

        t.AddField(ArrayField("secretaries",
            "The FULL list of ALL company secretaries. A secretary may be an individual OR a body corporate (a company acting as secretary).",
            F("name", "The secretary's full name (if an individual)."),
            F("is_body_corporate", "Whether the secretary is a company / body corporate rather than a person (true/false)."),
            F("corporate_name", "If the secretary is a body corporate, the company name of the corporate secretary."),
            F("corporate_number", "If the secretary is a body corporate, its company registration number."),
            F("address", "The secretary's address (residential if a person, registered office if corporate)."),
            F("email", "The secretary's email address, if shown.")));

        t.AddField(ArrayField("past_directors_and_secretaries",
            "Any PAST/former directors or secretaries listed (those who resigned or ceased during the period).",
            F("name", "The former officer's name."),
            F("role", "Whether they were a director or secretary."),
            F("date_of_resignation", "The date they resigned/ceased, if shown.", FieldDataFormat.Date)));

        t.AddField(ArrayField("shareholders",
            "The FULL list of ALL members / shareholders and their shareholdings. Return every shareholder, one array item each - capture all of them.",
            F("name", "The shareholder / member's name."),
            F("address", "The shareholder's address, if shown."),
            F("email", "The shareholder's email address, if shown."),
            F("share_class", "The class of shares held (e.g. Ordinary)."),
            F("number_of_shares", "The number of shares held.", FieldDataFormat.Number),
            F("nominal_value_per_share", "The nominal value per share."),
            F("amount_paid", "The amount paid or considered paid on the shares, if shown."),
            F("is_body_corporate", "Whether this member is a company / body corporate (true/false)."),
            F("corporate_number", "If the member is a body corporate, its company registration number.")));

        t.AddField(ArrayField("past_members",
            "Any PAST members / shareholders who transferred out or ceased to be members during the period, if listed.",
            F("name", "The former member's name."),
            F("number_of_shares_transferred", "Number of shares transferred/ceased, if shown.", FieldDataFormat.Number),
            F("date_of_transfer", "Date of transfer / cessation, if shown.", FieldDataFormat.Date)));
    }
}
