#:project ../../tests/LayaSample.Tests/LayaSample.Tests.csproj
#:property RestorePackagesWithLockFile=false

// Generates the lending corpus in docs/samples/pdf/corpus: loan applications, UCC filings and loan and security
// agreements for three fictional deals, each rendered as a digital PDF, a fillable form, a scan, a fax or a mix.
//   dotnet run docs/samples/generate-corpus.cs
// Output is deterministic. Set CORPUS_PREVIEW=<dir> to also write a PNG of every page for a visual check.

using System.Globalization;
using System.Text;
using System.Text.Json;
using ImageMagick;
using SkiaSharp;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var root = Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntrySKPointilePath") as string ?? "docs/samples/generate-corpus.cs"))!;
Corpus.Run(Path.Combine(root, "pdf", "corpus"), Environment.GetEnvironmentVariable("CORPUS_PREVIEW"));

// ---------------------------------------------------------------------------------------------------------------
// Drawing model: pages are lists of ops in PDF points with a top-left origin (US Letter, 612 x 792).
// ---------------------------------------------------------------------------------------------------------------

enum Face { Sans, SansBold, Serif, SerifBold, SerifItalic, Mono }

abstract record Op;
record TextOp(float X, float Y, float Size, Face Face, string Text) : Op;
record LineOp(float X1, float Y1, float X2, float Y2, float Width) : Op;
record BoxOp(float X, float Y, float W, float H, float Fill) : Op; // Fill: gray 0..1, or -1 for outline only
record CheckOp(string Name, float X, float Y, float Size, bool On) : Op;
record FieldOp(string Name, float X, float Y, float W, float H, string Value, float Size, bool Multiline) : Op;
record SquiggleOp(float X, float Y, float W, float H, int Seed) : Op; // a hand-drawn signature
record StampOp(float X, float Y, float Angle, float W, float H, string[] Lines) : Op; // Angle: degrees, counter-clockwise

sealed class Page
{
    public readonly List<Op> Ops = [];

    public Page T(float x, float y, string s, float size = 8, Face face = Face.Sans) { Ops.Add(new TextOp(x, y, size, face, s)); return this; }
    public Page TC(float cx, float y, string s, float size = 8, Face face = Face.Sans) => T(cx - Corpus.Measure(s, size, face) / 2, y, s, size, face);
    public Page L(float x1, float y1, float x2, float y2, float w = 0.6f) { Ops.Add(new LineOp(x1, y1, x2, y2, w)); return this; }
    public Page B(float x, float y, float w, float h, float fill = -1) { Ops.Add(new BoxOp(x, y, w, h, fill)); return this; }
    public Page Sign(float x, float y, float w, float h, int seed) { Ops.Add(new SquiggleOp(x, y, w, h, seed)); return this; }
    public Page Stamp(float x, float y, float angle, float w, params string[] lines) { Ops.Add(new StampOp(x, y, angle, w, 6 + lines.Length * 11, lines)); return this; }

    public Page F(string name, float x, float y, float w, float h, string value, float size = 9, bool multiline = false)
    {
        Ops.Add(new BoxOp(x, y, w, h, -1));
        Ops.Add(new FieldOp(name, x, y, w, h, value, size, multiline));
        return this;
    }

    public Page C(string name, float x, float y, bool on, string? label = null, float labelSize = 7.5f)
    {
        Ops.Add(new CheckOp(name, x, y, 8, on));
        if (label is not null) T(x + 11, y + 7, label, labelSize);
        return this;
    }
}

sealed record Deal
{
    public required string Slug { get; init; }
    public required string Borrower { get; init; }
    public required string EntityType { get; init; } // "limited liability company"
    public required string State { get; init; } // "Oregon"
    public required string Ein { get; init; }
    public required DateOnly Established { get; init; }
    public required string Street { get; init; }
    public required string City { get; init; }
    public required string StateAbbr { get; init; }
    public required string Zip { get; init; }
    public required string Phone { get; init; }
    public required string Email { get; init; }
    public required string Naics { get; init; }
    public required int Employees { get; init; }
    public required string Lender { get; init; }
    public required string LenderStreet { get; init; }
    public required string LenderCityLine { get; init; }
    public required string LenderPhone { get; init; }
    public required string LenderEmail { get; init; }
    public required string LoanType { get; init; }
    public required decimal Amount { get; init; }
    public required string RateKind { get; init; } // Fixed / Variable
    public required string RateText { get; init; }
    public required int TermMonths { get; init; }
    public required string Fee { get; init; }
    public required string Purpose { get; init; }
    public required string Repayment { get; init; }
    public required string[] Collateral { get; init; } // keys of Corpus.CollateralKinds
    public bool Blanket { get; init; }
    public required (string Description, string Serial, string Value)[] Equipment { get; init; }
    public required (string Name, string Title, int Pct, string Ssn4)[] Owners { get; init; }
    public required (string Institution, string Contact, string Phone, string Account)[] References { get; init; }
    public required decimal Revenue { get; init; }
    public required decimal NetIncome { get; init; }
    public required decimal Assets { get; init; }
    public required decimal Liabilities { get; init; }
    public required decimal ExistingDebt { get; init; }
    public required string ApplicationNo { get; init; }
    public required string LoanOfficer { get; init; }
    public required DateOnly ApplicationDate { get; init; }
    public required DateOnly AgreementDate { get; init; }
    public required string FilingOffice { get; init; }
    public required string UccFileNo { get; init; }
    public required DateOnly UccDate { get; init; }
    public required string Ucc3FileNo { get; init; }
    public required DateOnly Ucc3Date { get; init; }
    public required string Ucc3Kind { get; init; } // Assignment / Termination / Amendment
    public required string SignerName { get; init; }
    public required string SignerTitle { get; init; }
    public required string LenderSigner { get; init; }
    public required string LenderSignerTitle { get; init; }
}

static class Corpus
{
    const float Width = 612, Height = 792;
    static readonly string[] BaseFonts = ["Helvetica", "Helvetica-Bold", "Times-Roman", "Times-Bold", "Times-Italic", "Courier"];
    static readonly Dictionary<Face, SKTypeface> Typefaces = new()
    {
        [Face.Sans] = SKTypeface.FromFamilyName("Helvetica"),
        [Face.SansBold] = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold),
        [Face.Serif] = SKTypeface.FromFamilyName("Times"),
        [Face.SerifBold] = SKTypeface.FromFamilyName("Times", SKFontStyle.Bold),
        [Face.SerifItalic] = SKTypeface.FromFamilyName("Times", SKFontStyle.Italic),
        [Face.Mono] = SKTypeface.FromFamilyName("Courier"),
    };

    public static readonly Dictionary<string, (string Label, string Short, string Legal)> CollateralKinds = new()
    {
        ["realestate"] = ("Real estate", "Real Property", "all real property and fixtures, together with all improvements and appurtenances"),
        ["equipment"] = ("Equipment", "Equipment", "all Equipment, machinery, vehicles, tools, furniture and fixtures, including the items listed on Schedule A, and all accessions, additions, replacements and substitutions"),
        ["inventory"] = ("Inventory", "Inventory", "all Inventory, including raw materials, work in process, finished goods and goods held for sale or lease"),
        ["accounts"] = ("Accounts receivable", "Accounts", "all Accounts, chattel paper, instruments, notes and other rights to payment, and all Supporting Obligations"),
        ["deposit"] = ("Deposit accounts", "Deposit Accounts", "all Deposit Accounts and funds on deposit with Lender"),
        ["intangibles"] = ("General intangibles", "General Intangibles", "all General Intangibles, including payment intangibles, software, licenses, permits, trademarks and customer lists"),
    };

    static string Dollars(decimal v) => "$" + v.ToString("N0");
    static string Money(decimal v) => "$" + v.ToString("N2");
    static string Long(DateOnly d) => d.ToString("MMMM d, yyyy");
    static string Short(DateOnly d) => d.ToString("MM/dd/yyyy");

    static decimal Payment(decimal principal, double annualRate, int months)
    {
        var r = annualRate / 12;
        return Math.Round((decimal)((double)principal * r / (1 - Math.Pow(1 + r, -months))), 2);
    }

    static readonly Deal[] Deals =
    [
        new()
        {
            Slug = "redwood-mechanical", Borrower = "Redwood Mechanical Supply, LLC", EntityType = "limited liability company", State = "Oregon",
            Ein = "00-0412876", Established = new(2009, 3, 17), Street = "4820 SE Powell Industrial Way", City = "Portland", StateAbbr = "OR", Zip = "97206",
            Phone = "(503) 555-0142", Email = "ap@redwoodmech.example.com", Naics = "423830", Employees = 28,
            Lender = "Cascade Harbor Bank, N.A.", LenderStreet = "100 SW Front Avenue, Suite 900", LenderCityLine = "Portland, OR 97204",
            LenderPhone = "(503) 555-0100", LenderEmail = "loandocs@cascadeharbor.example.com",
            LoanType = "Term Loan", Amount = 750_000m, RateKind = "Fixed", RateText = "a fixed rate of 7.25% per annum", TermMonths = 84, Fee = "one-half of one percent (0.50%)",
            Purpose = "Purchase of CNC machining equipment and working capital",
            Repayment = $"Borrower shall repay the Loan in 84 consecutive monthly installments of principal and interest, each in the amount of {Money(Payment(750_000m, 0.0725, 84))}, due on the 10th day of each month commencing March 10, 2026. The entire unpaid principal balance and all accrued interest shall be due and payable on February 10, 2033 (the \"Maturity Date\").",
            Collateral = ["equipment", "inventory", "accounts"],
            Equipment =
            [
                ("CNC vertical machining center, 40-inch X-axis", "SN 1138820", "$118,500"),
                ("CNC turning center with live tooling", "SN 3604417", "$96,200"),
                ("Coordinate measuring machine, bridge type", "SN 0027731", "$74,900"),
                ("CNC vertical machining center, 42-inch X-axis", "SN VM42-0916", "$131,000"),
            ],
            Owners = [("Marcus T. Hale", "Managing Member", 60, "4417"), ("Elena R. Vasquez", "Member", 40, "9082")],
            References = [("Pacific Western Savings", "Joan Pruitt", "(503) 555-0161", "Operating account"), ("Columbia Valley Steel Co.", "Accounts Receivable", "(503) 555-0178", "Trade reference")],
            Revenue = 6_420_000m, NetIncome = 512_000m, Assets = 3_180_000m, Liabilities = 1_640_000m, ExistingDebt = 410_000m,
            ApplicationNo = "CA-2026-0117", LoanOfficer = "Dana K. Whitfield", ApplicationDate = new(2026, 1, 14), AgreementDate = new(2026, 2, 10),
            FilingOffice = "Oregon Secretary of State, UCC Division", UccFileNo = "2026-004417-8", UccDate = new(2026, 2, 12),
            Ucc3FileNo = "2026-009826-3", Ucc3Date = new(2026, 6, 22), Ucc3Kind = "Assignment",
            SignerName = "Marcus T. Hale", SignerTitle = "Managing Member", LenderSigner = "Dana K. Whitfield", LenderSignerTitle = "Senior Vice President",
        },
        new()
        {
            Slug = "blue-heron-dental", Borrower = "Blue Heron Dental Partners, P.C.", EntityType = "professional corporation", State = "Washington",
            Ein = "00-0733194", Established = new(2014, 8, 4), Street = "2210 Pacific Avenue, Suite 300", City = "Tacoma", StateAbbr = "WA", Zip = "98402",
            Phone = "(253) 555-0187", Email = "office@blueheron.example.com", Naics = "621210", Employees = 14,
            Lender = "Evergreen Commercial Finance Co.", LenderStreet = "1201 Third Avenue, Suite 2200", LenderCityLine = "Seattle, WA 98101",
            LenderPhone = "(206) 555-0166", LenderEmail = "documents@evergreenfinance.example.com",
            LoanType = "Equipment Term Loan", Amount = 320_000m, RateKind = "Variable",
            RateText = "a variable rate per annum equal to the Prime Rate as published in The Wall Street Journal plus 1.50%, adjusting on the first day of each calendar quarter",
            TermMonths = 60, Fee = "three-quarters of one percent (0.75%)",
            Purpose = "Acquisition of digital imaging and chairside milling equipment for a second location",
            Repayment = "Borrower shall make 60 consecutive monthly payments of principal and interest, each in an amount sufficient to fully amortize the Loan over its term, due on the 27th day of each month commencing April 27, 2026. The entire unpaid principal balance and all accrued interest shall be due and payable on March 27, 2031 (the \"Maturity Date\").",
            Collateral = ["equipment", "intangibles"],
            Equipment =
            [
                ("Intraoral 3D scanner with cart", "SN EM-220481", "$28,400"),
                ("Cone-beam CT and panoramic imaging unit", "SN PM3D-775013", "$96,000"),
                ("Chairside milling unit", "SN PMILL-90412", "$54,750"),
                ("Dental operatory chair package (4)", "SN PC-4410 to 4413", "$41,600"),
            ],
            Owners = [("Dr. Priya N. Raman", "President", 55, "2236"), ("Dr. Samuel O. Adeyemi", "Secretary", 45, "7715")],
            References = [("Harborview Community Bank", "Lucas Ferreira", "(253) 555-0192", "Operating account"), ("Summit Dental Supply", "Credit Department", "(800) 555-0144", "Trade reference")],
            Revenue = 2_980_000m, NetIncome = 455_000m, Assets = 1_420_000m, Liabilities = 690_000m, ExistingDebt = 285_000m,
            ApplicationNo = "EC-26-00342", LoanOfficer = "Tomas Berglund", ApplicationDate = new(2026, 3, 2), AgreementDate = new(2026, 3, 27),
            FilingOffice = "Washington Department of Licensing, UCC Filing Office", UccFileNo = "2026-089-4471-3", UccDate = new(2026, 3, 30),
            Ucc3FileNo = "2026-271-0958-6", Ucc3Date = new(2026, 9, 30), Ucc3Kind = "Termination",
            SignerName = "Dr. Priya N. Raman", SignerTitle = "President", LenderSigner = "Tomas Berglund", LenderSignerTitle = "Vice President",
        },
        new()
        {
            Slug = "sunbelt-freight", Borrower = "Sunbelt Freight Logistics, Inc.", EntityType = "corporation", State = "Texas",
            Ein = "00-0295518", Established = new(2003, 11, 21), Street = "1650 Industrial Boulevard", City = "Dallas", StateAbbr = "TX", Zip = "75207",
            Phone = "(214) 555-0129", Email = "finance@sunbeltfreight.example.com", Naics = "484121", Employees = 96,
            Lender = "Lone Star Capital Bank", LenderStreet = "500 Main Street, Suite 1200", LenderCityLine = "Fort Worth, TX 76102",
            LenderPhone = "(817) 555-0133", LenderEmail = "creditadmin@lonestarcapital.example.com",
            LoanType = "Revolving Line of Credit", Amount = 2_500_000m, RateKind = "Variable",
            RateText = "a variable rate per annum equal to the Prime Rate as published in The Wall Street Journal plus 0.75%, adjusting on each change in the Prime Rate",
            TermMonths = 24, Fee = "thirty-five hundredths of one percent (0.35%)",
            Purpose = "Working capital, funded by a revolving line secured by accounts and equipment",
            Repayment = "Borrower shall pay accrued interest monthly in arrears on the 5th day of each month commencing July 5, 2026. The entire unpaid principal balance and all accrued interest shall be due and payable on June 5, 2028 (the \"Maturity Date\"). Borrower may borrow, repay and reborrow up to the principal amount, subject to a borrowing base equal to 80% of Eligible Accounts plus 50% of Eligible Inventory.",
            Collateral = ["accounts", "equipment", "inventory", "deposit", "intangibles"], Blanket = true,
            Equipment =
            [
                ("2024 Class 8 sleeper tractor", "VIN 3AKJHHDR5RSLM0412", "$148,000"),
                ("2024 Class 8 sleeper tractor", "VIN 3AKJHHDR7RSLM0413", "$148,000"),
                ("53-foot dry van trailer", "VIN 1UYVS2536PM917204", "$42,500"),
                ("53-foot dry van trailer", "VIN 1UYVS2538PM917205", "$42,500"),
            ],
            Owners = [("Raymond J. Okafor", "Chief Executive Officer", 70, "3358"), ("Linda M. Park", "Chief Financial Officer", 30, "6641")],
            References = [("Trinity National Bank", "Alicia Navarro", "(214) 555-0153", "Operating account"), ("Gulf Coast Fuel Supply", "Credit Department", "(713) 555-0120", "Trade reference")],
            Revenue = 18_750_000m, NetIncome = 1_240_000m, Assets = 9_860_000m, Liabilities = 5_410_000m, ExistingDebt = 1_975_000m,
            ApplicationNo = "LS-2026-0508", LoanOfficer = "Beth A. Morales", ApplicationDate = new(2026, 5, 11), AgreementDate = new(2026, 6, 5),
            FilingOffice = "Texas Secretary of State, UCC Section", UccFileNo = "26-00281946", UccDate = new(2026, 6, 9),
            Ucc3FileNo = "26-00417305", Ucc3Date = new(2026, 8, 17), Ucc3Kind = "Amendment",
            SignerName = "Raymond J. Okafor", SignerTitle = "Chief Executive Officer", LenderSigner = "Beth A. Morales", LenderSignerTitle = "Executive Vice President",
        },
    ];

    // ---- orchestration -------------------------------------------------------------------------------------------

    sealed record Entry(string File, string Borrower, string DocumentType, string Rendering, string ExpectedKind, int Pages, Dictionary<string, string> Facts);

    public static void Run(string dir, string? previewDir)
    {
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "*.pdf")) File.Delete(old);
        var manifest = new List<Entry>();

        foreach (var deal in Deals)
        {
            var ucc3Variant = deal.Ucc3Kind switch { "Assignment" => "form", "Termination" => "scan", _ => "fax" };
            var jobs = new (string Type, string Variant)[]
            {
                ("loan-application", "digital"), ("loan-application", "form"), ("loan-application", "scan"), ("loan-application", "fax"),
                ("ucc1", "digital"), ("ucc1", "form"), ("ucc1", "scan"), ("ucc1", "fax"),
                ("ucc3", ucc3Variant),
                ("loan-security-agreement", "digital"), ("loan-security-agreement", "mixed"), ("loan-security-agreement", "scan"), ("loan-security-agreement", "fax"),
            };
            foreach (var (type, variant) in jobs)
            {
                var name = $"{deal.Slug}-{type}-{variant}.pdf";
                var pages = type switch
                {
                    "loan-application" => LoanApplication(deal),
                    "ucc1" => Ucc1(deal),
                    "ucc3" => Ucc3(deal),
                    _ => LoanAgreement(deal),
                };
                var title = $"{deal.Borrower} - {type}";
                var seed = name.Aggregate(17, (a, c) => unchecked(a * 31 + c));
                var rng = new Random(seed);
                var sender = type == "loan-application" ? deal.Borrower : deal.Lender;
                var senderPhone = type == "loan-application" ? deal.Phone : deal.LenderPhone;
                var bytes = variant switch
                {
                    "digital" => BuildVector(pages, false, title),
                    "form" => BuildVector(pages, true, title),
                    "scan" => BuildScanned(pages, 0, rng, title),
                    "mixed" => BuildScanned(pages, Math.Max(0, pages.Count - 2), rng, title),
                    "fax" => BuildFax(pages, rng, DocDate(deal, type), sender, senderPhone, type == "loan-security-agreement" ? 196 : 98),
                    _ => throw new InvalidOperationException(variant),
                };
                File.WriteAllBytes(Path.Combine(dir, name), bytes);
                manifest.Add(new(name, deal.Borrower, type, variant, ExpectedKind(type, variant), pages.Count, Facts(deal, type)));
                Console.WriteLine($"{name,-58} {pages.Count,2} pages {bytes.Length,10:N0} bytes");

                if (previewDir is not null)
                {
                    Directory.CreateDirectory(previewDir);
                    for (var i = 0; i < pages.Count; i++)
                    {
                        using var bmp = Raster(pages[i], 100);
                        File.WriteAllBytes(Path.Combine(previewDir, $"{Path.GetFileNameWithoutExtension(name)}-p{i + 1}.png"), Png(bmp));
                    }
                }
            }
        }

        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }) + "\n");
    }

    // The classification.kind the API reports. Form-like pages (applications, UCC filings) carry many drawn boxes, so
    // their digital renderings are LayoutPdf; only the running-text agreement is TextPdf.
    static string ExpectedKind(string type, string variant) => variant switch
    {
        "digital" => type == "loan-security-agreement" ? "TextPdf" : "LayoutPdf",
        "form" => "FormPdf",
        "scan" => "ScannedPdf",
        "fax" => "FaxPdf",
        _ => "MixedPdf",
    };

    static DateOnly DocDate(Deal d, string type) => type switch
    {
        "loan-application" => d.ApplicationDate,
        "ucc1" => d.UccDate,
        "ucc3" => d.Ucc3Date,
        _ => d.AgreementDate,
    };

    // Ground truth for extraction tests: what each document type says.
    static Dictionary<string, string> Facts(Deal d, string type)
    {
        var facts = new Dictionary<string, string> { ["debtorName"] = d.Borrower, ["lender"] = d.Lender };
        switch (type)
        {
            case "loan-application":
                facts["ein"] = d.Ein;
                facts["amountRequested"] = Dollars(d.Amount);
                facts["termMonths"] = d.TermMonths.ToString();
                facts["applicationDate"] = Short(d.ApplicationDate);
                facts["applicationNo"] = d.ApplicationNo;
                break;
            case "ucc1":
                facts["fileNumber"] = d.UccFileNo;
                facts["filingDate"] = Short(d.UccDate);
                facts["filingOffice"] = d.FilingOffice;
                facts["collateral"] = string.Join(", ", d.Collateral.Select(k => CollateralKinds[k].Short));
                break;
            case "ucc3":
                facts["initialFileNumber"] = d.UccFileNo;
                facts["fileNumber"] = d.Ucc3FileNo;
                facts["filingDate"] = Short(d.Ucc3Date);
                facts["action"] = d.Ucc3Kind;
                break;
            default:
                facts["agreementDate"] = Short(d.AgreementDate);
                facts["principal"] = Money(d.Amount);
                facts["loanType"] = d.LoanType;
                facts["governingLaw"] = d.State;
                break;
        }
        return facts;
    }

    // ---- shared layout helpers -----------------------------------------------------------------------------------

    public static float Measure(string s, float size, Face face)
    {
        using var font = new SKFont(Typefaces[face], size);
        return font.MeasureText(s);
    }

    static List<string> Wrap(string text, float size, Face face, float width, float firstOffset = 0)
    {
        var lines = new List<string>();
        var current = "";
        var available = width - firstOffset;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && Measure(candidate, size, face) > available * 0.98f)
            {
                lines.Add(current);
                current = word;
                available = width;
            }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    static List<string> FieldLines(FieldOp f) =>
        f.Value.Split('\n').SelectMany(p => Wrap(p, f.Size, Face.Mono, f.W - 6)).ToList();

    static float Section(Page p, float y, string title)
    {
        p.B(36, y, 540, 13, 0.88f).T(40, y + 9.5f, title, 8, Face.SansBold);
        return y + 19;
    }

    // A row of labelled fields across the page; weights give relative widths.
    static float Row(Page p, float y, params (string Label, string Name, string Value, float Weight)[] cols)
    {
        const float left = 36, total = 540, gap = 6;
        var sum = cols.Sum(c => c.Weight);
        var available = total - gap * (cols.Length - 1);
        var x = left;
        foreach (var c in cols)
        {
            var w = available * c.Weight / sum;
            p.T(x, y + 5, c.Label, 6).F(c.Name, x, y + 8, w, 15, c.Value);
            x += w + gap;
        }
        return y + 28;
    }

    static void Footer(Page p, string text, int n, int total) =>
        p.L(36, 750, 576, 750, 0.4f).T(36, 762, text, 6.5f).T(576 - Measure($"Page {n} of {total}", 6.5f, Face.Sans), 762, $"Page {n} of {total}", 6.5f);

    // ---- documents -----------------------------------------------------------------------------------------------

    static List<Page> LoanApplication(Deal d)
    {
        var p1 = new Page();
        p1.TC(306, 46, "COMMERCIAL LOAN APPLICATION", 16, Face.SansBold);
        p1.TC(306, 60, $"{d.Lender}  |  {d.LenderStreet}, {d.LenderCityLine}", 8);
        p1.T(36, 80, $"Application date: {Short(d.ApplicationDate)}", 8, Face.SansBold);
        var y = Section(p1, 88, "1. APPLICANT INFORMATION");
        y = Row(p1, y, ("LEGAL NAME OF BUSINESS", "applicant_name", d.Borrower, 3), ("DOING BUSINESS AS", "applicant_dba", "", 2));
        y = Row(p1, y, ("ENTITY TYPE", "entity_type", Cap(d.EntityType), 2.2f), ("STATE OF ORGANIZATION", "state_org", d.State, 1.4f), ("FEDERAL EIN", "ein", d.Ein, 1.4f), ("DATE ESTABLISHED", "established", Short(d.Established), 1.3f));
        y = Row(p1, y, ("STREET ADDRESS", "street", d.Street, 3), ("CITY", "city", d.City, 1.5f), ("STATE", "state", d.StateAbbr, 0.6f), ("ZIP", "zip", d.Zip, 1));
        y = Row(p1, y, ("BUSINESS PHONE", "phone", d.Phone, 1.5f), ("EMAIL", "email", d.Email, 2.7f), ("NAICS CODE", "naics", d.Naics, 1), ("EMPLOYEES", "employees", d.Employees.ToString(), 1));

        y = Section(p1, y + 2, "2. LOAN REQUEST");
        y = Row(p1, y, ("TYPE OF LOAN", "loan_type", d.LoanType, 2.2f), ("AMOUNT REQUESTED", "amount", Dollars(d.Amount), 1.5f), ("TERM (MONTHS)", "term", d.TermMonths.ToString(), 1.1f), ("RATE PREFERENCE", "rate_pref", d.RateKind, 1.3f));
        y = Row(p1, y, ("PURPOSE OF LOAN", "purpose", d.Purpose, 1));
        p1.T(36, y + 6, "COLLATERAL OFFERED", 6);
        var x = 36f;
        foreach (var (key, label) in CollateralKinds.Select(k => (k.Key, k.Value.Label)).Append(("blanket", "Blanket lien")))
        {
            var on = key == "blanket" ? d.Blanket : d.Collateral.Contains(key);
            p1.C($"collateral_{key}", x, y + 11, on, label);
            x += 12 + Measure(label, 7.5f, Face.Sans) + 12;
        }
        y += 30;

        y = Section(p1, y, "3. PRINCIPAL OWNERS (20% OR MORE)");
        float[] cw = [230, 160, 70, 80];
        string[] heads = ["NAME", "TITLE", "% OWNED", "SSN (LAST 4)"];
        x = 36;
        for (var i = 0; i < 4; i++) { p1.T(x + 2, y + 5, heads[i], 6); x += cw[i]; }
        y += 8;
        for (var r = 0; r < 3; r++)
        {
            var o = r < d.Owners.Length ? d.Owners[r] : default;
            x = 36;
            string[] vals = [o.Name ?? "", o.Title ?? "", r < d.Owners.Length ? o.Pct.ToString() : "", r < d.Owners.Length ? "xxx-xx-" + o.Ssn4 : ""];
            for (var i = 0; i < 4; i++)
            {
                p1.F($"owner{r + 1}_{i}", x, y, cw[i], 15, vals[i]);
                x += cw[i];
            }
            y += 15;
        }
        y += 8;

        y = Section(p1, y, "4. FINANCIAL SUMMARY (LAST FISCAL YEAR)");
        Row(p1, y, ("ANNUAL REVENUE", "revenue", Dollars(d.Revenue), 1), ("NET INCOME", "net_income", Dollars(d.NetIncome), 1), ("TOTAL ASSETS", "assets", Dollars(d.Assets), 1), ("TOTAL LIABILITIES", "liabilities", Dollars(d.Liabilities), 1), ("EXISTING DEBT", "existing_debt", Dollars(d.ExistingDebt), 1));
        Footer(p1, $"Commercial Loan Application - {d.Borrower}", 1, 2);

        var p2 = new Page();
        y = Section(p2, 44, "5. BANK AND TRADE REFERENCES");
        float[] rw = [170, 120, 100, 150];
        string[] rh = ["INSTITUTION / COMPANY", "CONTACT", "PHONE", "ACCOUNT / RELATIONSHIP"];
        x = 36;
        for (var i = 0; i < 4; i++) { p2.T(x + 2, y + 5, rh[i], 6); x += rw[i]; }
        y += 8;
        for (var r = 0; r < 3; r++)
        {
            x = 36;
            string[] vals = r < d.References.Length ? [d.References[r].Institution, d.References[r].Contact, d.References[r].Phone, d.References[r].Account] : ["", "", "", ""];
            for (var i = 0; i < 4; i++)
            {
                p2.F($"ref{r + 1}_{i}", x, y, rw[i], 15, vals[i]);
                x += rw[i];
            }
            y += 15;
        }
        y += 10;

        y = Section(p2, y, "6. DISCLOSURES");
        string[] questions =
        [
            "Is the applicant a party to any pending lawsuit or legal proceeding?",
            "Has the applicant or any principal owner filed for bankruptcy in the last seven years?",
            "Are any federal, state or payroll taxes past due?",
            "Is any principal owner delinquent on a federal debt or guaranty?",
        ];
        for (var i = 0; i < questions.Length; i++)
        {
            p2.T(40, y + 8, questions[i], 8).C($"q{i + 1}_yes", 440, y, false, "Yes").C($"q{i + 1}_no", 500, y, true, "No");
            y += 15;
        }
        y += 8;

        y = Section(p2, y, "7. CERTIFICATION AND AUTHORIZATION");
        var cert = $"The undersigned certifies that the information in this application and in any attached statements is true, complete and correct, and is given to induce {d.Lender} (\"Lender\") to extend credit. The undersigned authorizes Lender to obtain credit reports, to verify the information provided with any person or institution named, and, if credit is extended, to file financing statements under the Uniform Commercial Code describing the collateral. This application does not constitute a commitment to lend.";
        foreach (var line in Wrap(cert, 8, Face.Serif, 530))
        {
            p2.T(40, y + 8, line, 8, Face.Serif);
            y += 10.5f;
        }
        y += 22;
        p2.Sign(46, y - 16, 170, 22, d.SignerName.Length * 7).L(40, y + 8, 260, y + 8);
        p2.T(40, y + 17, "Signature of authorized representative", 6);
        p2.T(280, y + 17, "Date", 6).L(280, y + 8, 360, y + 8).T(284, y + 5, Short(d.ApplicationDate), 9, Face.Mono);
        y += 28;
        y = Row(p2, y, ("PRINT NAME", "signer_name", d.SignerName, 2), ("TITLE", "signer_title", d.SignerTitle, 2));

        y += 6;
        p2.B(36, y, 540, 74, 0.95f).T(42, y + 12, "FOR LENDER USE ONLY", 8, Face.SansBold);
        Row(p2, y + 16, ("APPLICATION NO.", "lender_app_no", d.ApplicationNo, 1.2f), ("RECEIVED BY", "lender_officer", d.LoanOfficer, 1.5f), ("DATE RECEIVED", "lender_received", Short(d.ApplicationDate), 1));
        p2.C("decision_approved", 42, y + 60, false, "Approved").C("decision_declined", 110, y + 60, false, "Declined").C("decision_review", 178, y + 60, true, "Under review");
        Footer(p2, $"Commercial Loan Application - {d.Borrower}", 2, 2);
        return [p1, p2];
    }

    static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    // A bordered cell with a small caption in its top-left corner and the value underneath, as on the national forms.
    static void Cell(Page p, float x, float y, float w, float h, string label, string? name = null, string value = "", bool multiline = false, float size = 8.5f)
    {
        p.B(x, y, w, h);
        var captions = label.Split('\n');
        for (var i = 0; i < captions.Length; i++) p.T(x + 2, y + 6.5f + i * 6, captions[i], 5.5f);
        if (name is null) return;
        var inset = multiline ? 8 : 0;
        p.Ops.Add(new FieldOp(name, x, y + inset, w, h - inset, value, size, multiline));
    }

    static float Cells(Page p, float y, float h, params (string Label, string? Name, string Value, float Width)[] cols)
    {
        var x = 36f;
        foreach (var c in cols)
        {
            Cell(p, x, y, c.Width, h, c.Label, c.Name, c.Value);
            x += c.Width;
        }
        return y + h;
    }

    static void ItemHead(Page p, float y, string bold, string rest = "")
    {
        p.T(38, y, bold, 7, Face.SansBold);
        if (rest.Length > 0) p.T(38 + Measure(bold, 7, Face.SansBold) + 3, y, rest, 5.5f);
    }

    // Top of UCC1 and UCC3 (rev. 05/22/02): filing-office bars, title, boxes A and B, and the filing-office-use space.
    static float UccHeader(Page p, Deal d, string title, DateOnly filed, string fileNo)
    {
        p.B(36, 18, 157, 13, 0).B(36, 36, 157, 4, 0).B(36, 44, 157, 4, 0).B(36, 52, 157, 13, 0);
        p.T(36, 88, title, 10, Face.SansBold).T(36, 98, "FOLLOW INSTRUCTIONS (front and back) CAREFULLY", 6.5f);
        Cell(p, 36, 104, 290, 24, "A. NAME & PHONE OF CONTACT AT FILER [optional]", "contact", $"Loan Documentation, {d.LenderPhone}");
        Cell(p, 36, 128, 290, 92, "B. SEND ACKNOWLEDGMENT TO:   (Name and Address)", "acknowledgment_to", $"{d.Lender}\n{d.LenderStreet}\n{d.LenderCityLine}", multiline: true);
        p.Stamp(380, 195, 4, 160, "FILED", d.FilingOffice.Split(',')[0], $"File No. {fileNo}", $"{Short(filed)}  09:14 AM");
        p.T(344, 230, "THE ABOVE SPACE IS FOR FILING OFFICE USE ONLY", 6.5f, Face.SansBold);
        return 238;
    }

    // Debtor (items 1 and 2) or secured party (item 3) block.
    static float Party(Page p, float y, string prefix, string head, string headNote, string org, string street, string city, string state, string zip, (string Type, string Jurisdiction, string OrgId)? orgInfo)
    {
        ItemHead(p, y + 6, head, headNote);
        y += 10;
        var n = prefix[0];
        Cell(p, 36, y, 540, 22, $"{prefix}a. ORGANIZATION'S NAME", $"party{n}_org", org);
        p.T(20, y + 33, "OR", 6.5f);
        y += 22;
        y = Cells(p, y, 22, ($"{prefix}b. INDIVIDUAL'S LAST NAME", $"party{n}_last", "", 240), ("FIRST NAME", $"party{n}_first", "", 140), ("MIDDLE NAME", $"party{n}_middle", "", 120), ("SUFFIX", $"party{n}_suffix", "", 40));
        y = Cells(p, y, 22, ($"{prefix}c. MAILING ADDRESS", $"party{n}_street", street, 240), ("CITY", $"party{n}_city", city, 140), ("STATE", $"party{n}_state", state, 40), ("POSTAL CODE", $"party{n}_zip", zip, 80), ("COUNTRY", $"party{n}_country", street.Length > 0 ? "USA" : "", 40));
        if (orgInfo is null) return y + 4;
        var x = 36f;
        Cell(p, x, y, 60, 26, $"{prefix}d. SEE\nINSTRUCTIONS");
        x += 60;
        Cell(p, x, y, 60, 26, "ADD'L INFO RE\nORGANIZATION\nDEBTOR");
        x += 60;
        Cell(p, x, y, 135, 26, $"{prefix}e. TYPE OF ORGANIZATION", $"party{n}_type", orgInfo.Value.Type);
        x += 135;
        Cell(p, x, y, 145, 26, $"{prefix}f. JURISDICTION OF ORGANIZATION", $"party{n}_jurisdiction", orgInfo.Value.Jurisdiction);
        x += 145;
        Cell(p, x, y, 140, 26, $"{prefix}g. ORGANIZATIONAL ID #, if any", $"party{n}_orgid", orgInfo.Value.OrgId);
        p.C($"party{n}_none", 576 - 34, y + 15, orgInfo.Value.OrgId.Length == 0 && orgInfo.Value.Type.Length > 0).T(576 - 23, y + 22, "NONE", 5.5f);
        return y + 30;
    }

    static List<Page> Ucc1(Deal d)
    {
        var p = new Page();
        var y = UccHeader(p, d, "UCC FINANCING STATEMENT", d.UccDate, d.UccFileNo);
        y = Party(p, y, "1", "1. DEBTOR'S EXACT FULL LEGAL NAME", "- insert only one debtor name (1a or 1b) - do not abbreviate or combine names",
            d.Borrower, d.Street, d.City, d.StateAbbr, d.Zip, (Cap(d.EntityType), d.State, "SOS-" + d.Ein[^6..]));
        y = Party(p, y, "2", "2. ADDITIONAL DEBTOR'S EXACT FULL LEGAL NAME", "- insert only one debtor name (2a or 2b) - do not abbreviate or combine names",
            "", "", "", "", "", ("", "", ""));
        y = Party(p, y, "3", "3. SECURED PARTY'S NAME", "(or NAME of TOTAL ASSIGNEE of ASSIGNOR S/P) - insert only one secured party name (3a or 3b)",
            d.Lender, d.LenderStreet, d.LenderCityLine.Split(',')[0], d.LenderCityLine.Split(", ")[1][..2], d.LenderCityLine[^5..], null);

        ItemHead(p, y + 8, "4. This FINANCING STATEMENT covers the following collateral:");
        p.F("collateral", 36, y + 12, 540, 690 - (y + 12), CollateralText(d), 8.5f, multiline: true);

        p.B(36, 698, 540, 14).T(38, 708, "5. ALTERNATIVE DESIGNATION [if applicable]:", 6);
        (string Key, string Label, float X)[] alts = [("lessee", "LESSEE/LESSOR", 172), ("consignee", "CONSIGNEE/CONSIGNOR", 240), ("bailee", "BAILEE/BAILOR", 335), ("seller", "SELLER/BUYER", 400), ("aglien", "AG. LIEN", 465), ("nonucc", "NON-UCC FILING", 505)];
        foreach (var (key, label, ax) in alts) p.C($"alt_{key}", ax, 701, false).T(ax + 10, 708, label, 5);
        p.B(36, 712, 300, 22).T(38, 720, "6. This FINANCING STATEMENT is to be filed [for record] (or recorded) in the REAL ESTATE RECORDS.", 5.5f)
            .T(38, 729, "Attach Addendum  [if applicable]", 5.5f).C("real_estate", 320, 718, false);
        p.B(336, 712, 240, 22).T(338, 720, "7. Check to REQUEST SEARCH REPORT(S) on Debtor(s)  [ADDITIONAL FEE]  [optional]", 5.5f)
            .C("search_all", 340, 723, false, "All Debtors", 6).C("search_1", 420, 723, false, "Debtor 1", 6).C("search_2", 490, 723, false, "Debtor 2", 6);
        Cell(p, 36, 734, 540, 18, "8. OPTIONAL FILER REFERENCE DATA", "filer_ref", $"{d.Lender.Split(' ')[0]} / Loan {d.ApplicationNo} / {d.LoanType}", size: 8);

        p.T(36, 768, "FILING OFFICE COPY - UCC FINANCING STATEMENT (FORM UCC1) (REV. 05/22/02)", 7, Face.SansBold);
        return [p];
    }

    static string CollateralText(Deal d) =>
        $"All of Debtor's right, title and interest in the following property, whether now owned or hereafter acquired: {string.Join(", ", d.Collateral.Select(k => CollateralKinds[k].Short))}, together with all accessions, substitutions, replacements, products and proceeds thereof. See the Loan and Security Agreement dated {Long(d.AgreementDate)} between Debtor and Secured Party.";

    // UCC3 (rev. 07/01/23), page 2 of the national form: the amendment itself.
    static List<Page> Ucc3(Deal d)
    {
        var p = new Page();
        var kind = d.Ucc3Kind;
        p.B(36, 20, 158, 13, 0).B(36, 41, 158, 4, 0).B(36, 51, 158, 4, 0).B(36, 63, 158, 13, 0);
        p.T(36, 97, "UCC FINANCING STATEMENT", 10, Face.SansBold).T(36 + Measure("UCC FINANCING STATEMENT ", 10, Face.SansBold), 97, "AMENDMENT", 11.5f, Face.SansBold);
        p.T(36, 106, "FOLLOW INSTRUCTIONS", 6.5f);
        Cell(p, 36, 112, 290, 30, "A. NAME & PHONE OF CONTACT AT SUBMITTER (optional)", "contact_name", $"Loan Documentation, {d.LenderPhone}");
        Cell(p, 36, 142, 290, 24, "B. E-MAIL CONTACT AT SUBMITTER (optional)", "contact_email", d.LenderEmail);
        Cell(p, 36, 166, 290, 62, "C. SEND ACKNOWLEDGMENT TO:   (Name and Address)", "acknowledgment_to", $"{d.Lender}\n{d.LenderStreet}\n{d.LenderCityLine}", multiline: true);
        p.T(80, 238, "SEE BELOW FOR SECURED PARTY CONTACT INFORMATION", 6.5f, Face.SansBold);
        p.T(360, 238, "THE ABOVE SPACE IS FOR FILING OFFICE USE ONLY", 6.5f, Face.SansBold);
        p.Stamp(380, 205, 4, 160, "FILED", d.FilingOffice.Split(',')[0], $"File No. {d.Ucc3FileNo}", $"{Short(d.Ucc3Date)}  09:14 AM");

        var y = 243f;
        p.L(36, y, 576, y, 1.5f);
        Cell(p, 36, y, 290, 26, "1a. INITIAL FINANCING STATEMENT FILE NUMBER", "initial_file_no", d.UccFileNo);
        p.C("real_estate", 332, y + 4, false).T(344, y + 8, "1b. This FINANCING STATEMENT AMENDMENT is to be filed [for record]", 5.5f)
            .T(344, y + 15, "(or recorded) in the REAL ESTATE RECORDS. Filer: attach Amendment Addendum", 5.5f).T(344, y + 22, "(Form UCC3Ad) and provide Debtor's name in item 13.", 5.5f);
        y += 26;

        void Item(string num, string key, string head, string text, bool on)
        {
            p.L(36, y, 576, y, 1.5f);
            p.T(38, y + 11, num, 7).C(key, 50, y + 4, on).T(64, y + 10, head, 6.5f, Face.SansBold);
            var lines = Wrap(text, 5.8f, Face.Sans, 510, Measure(head + "   ", 6.5f, Face.SansBold) + 28);
            for (var i = 0; i < lines.Count; i++)
                p.T(i == 0 ? 64 + Measure(head + "  ", 6.5f, Face.SansBold) : 64, y + 10 + i * 7.5f, lines[i], 5.8f);
            y += 24;
        }

        Item("2.", "chk_termination", "TERMINATION:", "Effectiveness of the Financing Statement identified above is terminated with respect to the security interest(s) of Secured Party(ies) authorizing this Termination Statement", kind == "Termination");
        Item("3.", "chk_assignment", "ASSIGNMENT:", "Provide name of Assignee in item 7a or 7b, and address of Assignee in item 7c and name of Assignor in item 9. For partial assignment, complete items 7 and 9; check ASSIGN Collateral box in Item 8 and describe the affected collateral in item 8", kind == "Assignment");
        Item("4.", "chk_continuation", "CONTINUATION:", "Effectiveness of the Financing Statement identified above with respect to the security interest(s) of Secured Party authorizing this Continuation Statement is continued for the additional period provided by applicable law", false);

        p.L(36, y, 576, y, 1);
        p.T(38, y + 10, "5.", 7).T(62, y + 10, "PARTY INFORMATION CHANGE:", 6.5f).T(62, y + 18, "Check one of these two boxes:", 5.5f);
        p.T(62, y + 27, "This Change affects", 5.5f).C("chg_debtor", 125, y + 20, false, "Debtor or", 5.5f).C("chg_secured", 172, y + 20, false, "Secured Party of record", 5.5f);
        p.T(262, y + 15, "AND  Check one of these three boxes to:", 5.5f);
        p.C("pic_change", 262, y + 19, false).T(273, y + 24, "CHANGE name and/or address: Complete", 5).T(273, y + 30, "item 6a or 6b; and item 7a or 7b and item 7c", 5);
        p.C("pic_add", 380, y + 19, false).T(391, y + 24, "ADD name: Complete item", 5).T(391, y + 30, "7a or 7b, and item 7c", 5);
        p.C("pic_delete", 458, y + 19, false).T(469, y + 24, "DELETE name: Give record name", 5).T(469, y + 30, "to be deleted in item 6a or 6b", 5);
        y += 34;

        p.L(36, y, 576, y, 1);
        p.T(38, y + 9, "6.", 7).T(50, y + 9, "CURRENT RECORD INFORMATION:", 6.5f, Face.SansBold).T(50 + Measure("CURRENT RECORD INFORMATION:", 6.5f, Face.SansBold) + 5, y + 9, "Complete for Party Information Change - provide only one name (6a or 6b)", 5.5f);
        y += 12;
        Cell(p, 36, y, 540, 22, "6a. ORGANIZATION'S NAME", "record_org", "");
        p.T(20, y + 33, "OR", 6.5f);
        y = Cells(p, y + 22, 22, ("6b. INDIVIDUAL'S SURNAME", "record_surname", "", 250), ("FIRST PERSONAL NAME", "record_first", "", 140), ("ADDITIONAL NAME(S)/INITIAL(S)", "record_middle", "", 110), ("SUFFIX", "record_suffix", "", 40));

        p.L(36, y + 2, 576, y + 2, 1);
        y += 2;
        p.T(38, y + 9, "7.", 7).T(50, y + 9, "CHANGED OR ADDED INFORMATION:", 6.5f, Face.SansBold).T(50 + Measure("CHANGED OR ADDED INFORMATION:", 6.5f, Face.SansBold) + 5, y + 9, "Complete for Assignment or Party Information Change - provide only one name (7a or 7b) (use exact, full name; do not omit, modify, or abbreviate any part of the Debtor's name)", 4.6f);
        y += 12;
        var assign = kind == "Assignment";
        Cell(p, 36, y, 540, 22, "7a. ORGANIZATION'S NAME", "assignee_org", assign ? "Pacific Rim Capital Partners, L.P." : "");
        p.T(20, y + 33, "OR", 6.5f);
        y += 22;
        Cell(p, 36, y, 540, 22, "7b. INDIVIDUAL'S SURNAME", "assignee_surname", "");
        y += 22;
        Cell(p, 36, y, 540, 22, "INDIVIDUAL'S FIRST PERSONAL NAME", "assignee_first", "");
        y = Cells(p, y + 22, 22, ("INDIVIDUAL'S ADDITIONAL NAME(S)/INITIAL(S)", "assignee_middle", "", 500), ("SUFFIX", "assignee_suffix", "", 40));
        y = Cells(p, y, 22, ("7c. MAILING ADDRESS", "assignee_street", assign ? "880 Market Street" : "", 240), ("CITY", "assignee_city", assign ? "San Francisco" : "", 140), ("STATE", "assignee_state", assign ? "CA" : "", 40), ("POSTAL CODE", "assignee_zip", assign ? "94102" : "", 80), ("COUNTRY", "assignee_country", assign ? "USA" : "", 40));

        p.L(36, y + 2, 576, y + 2, 1);
        y += 2;
        var add = kind == "Amendment";
        p.T(38, y + 10, "8.", 7).T(62, y + 10, "COLLATERAL CHANGE:", 6.5f, Face.SansBold).T(150, y + 10, "Check only one box:", 5.5f);
        p.C("chg_add", 262, y + 3, add, "ADD collateral", 5.5f).C("chg_delete", 330, y + 3, false, "DELETE collateral", 5.5f).C("chg_restate", 410, y + 3, false, "RESTATE covered collateral", 5.5f).C("chg_assign", 510, y + 3, false, "ASSIGN* collateral", 5.5f);
        p.T(62, y + 22, "Indicate collateral:", 5.5f).T(190, y + 19, "*Check ASSIGN COLLATERAL only if the assignee's power to amend the record is limited to certain collateral and describe the collateral in Section 8", 4.6f);
        p.F("collateral_change", 62, y + 26, 514, 560 + 96 - (y + 26) - 0, add ? "Add to the collateral described in the initial financing statement: all deposit accounts and general intangibles of Debtor, now owned or hereafter acquired, and all proceeds thereof." : "", 8.5f, multiline: true);
        y = 656;

        p.L(36, y, 576, y, 1.5f);
        p.T(38, y + 10, "9.", 7).T(50, y + 10, "NAME OF SECURED PARTY OF RECORD AUTHORIZING THIS AMENDMENT:", 6.5f, Face.SansBold).T(50 + Measure("NAME OF SECURED PARTY OF RECORD AUTHORIZING THIS AMENDMENT:", 6.5f, Face.SansBold) + 5, y + 10, "Provide only one name (9a or 9b) (name of Assignor, if this is an Assignment)", 5.5f);
        p.T(50, y + 18, "If this is an Amendment authorized by a DEBTOR, check here", 5.5f).C("authorized_by_debtor", 215, y + 12, false).T(227, y + 18, "and provide name of authorizing Debtor", 5.5f);
        y += 21;
        Cell(p, 36, y, 540, 22, "9a. ORGANIZATION'S NAME", "authorizing_org", d.Lender);
        p.T(20, y + 33, "OR", 6.5f);
        y = Cells(p, y + 22, 22, ("9b. INDIVIDUAL'S SURNAME", "authorizing_surname", "", 250), ("FIRST PERSONAL NAME", "authorizing_first", "", 140), ("ADDITIONAL NAME(S)/INITIAL(S)", "authorizing_middle", "", 110), ("SUFFIX", "authorizing_suffix", "", 40));
        Cell(p, 36, y + 2, 540, 22, "10. OPTIONAL FILER REFERENCE DATA:", "filer_ref", $"{d.Lender.Split(' ')[0]} / Loan {d.ApplicationNo} / UCC-3 {kind}", size: 8);
        p.L(36, 752, 576, 752, 1.5f);
        p.T(36, 766, "FILING OFFICE COPY - UCC FINANCING STATEMENT AMENDMENT (Form UCC3) (Rev. 07/01/23)", 7, Face.SansBold);
        return [p];
    }

    static List<Page> LoanAgreement(Deal d)
    {
        var f = new Flow();
        var entity = $"a {d.State} {d.EntityType}";
        f.Title("LOAN AND SECURITY AGREEMENT", 15);
        f.Gap(4);
        f.Center($"between {d.Borrower} and {d.Lender}", 10.5f);
        f.Center($"Dated as of {Long(d.AgreementDate)}", 10.5f);
        f.Gap(10);
        f.Para($"This LOAN AND SECURITY AGREEMENT (this \"Agreement\") is entered into as of {Long(d.AgreementDate)} by and between {d.Borrower}, {entity} (\"Borrower\"), with its principal place of business at {d.Street}, {d.City}, {d.StateAbbr} {d.Zip}, and {d.Lender} (\"Lender\"), with an office at {d.LenderStreet}, {d.LenderCityLine}.");
        f.Para($"Borrower has requested that Lender make a {d.LoanType.ToLowerInvariant()} to Borrower in the principal amount of {Money(d.Amount)}, the proceeds of which will be used for the following purpose: {char.ToLowerInvariant(d.Purpose[0])}{d.Purpose[1..]}. Lender is willing to make the Loan on the terms and subject to the conditions of this Agreement. In consideration of the mutual covenants below, the parties agree as follows:");

        f.Heading("ARTICLE 1 - DEFINITIONS AND CONSTRUCTION");
        f.Para("\"Collateral\" has the meaning given in Section 3.1. \"Event of Default\" has the meaning given in Section 6.1. \"Loan\" means the credit facility described in Section 2.1. \"Loan Documents\" means this Agreement, the Note, each financing statement, and every other agreement or instrument executed in connection with the Loan. \"Obligations\" means all principal, interest, fees, expenses and other amounts owing by Borrower to Lender under the Loan Documents. \"Permitted Liens\" means liens for taxes not yet delinquent and liens in favor of Lender. \"UCC\" means the Uniform Commercial Code as in effect in the State of " + d.State + ".", "1.1 Defined Terms.");
        f.Para("Headings are for convenience only. The words \"include\" and \"including\" are not words of limitation. Terms used and not defined in this Agreement that are defined in the UCC have the meanings given in the UCC.", "1.2 Construction.");

        f.Heading("ARTICLE 2 - THE LOAN");
        f.Para($"Subject to the terms of this Agreement, Lender agrees to make a {d.LoanType.ToLowerInvariant()} to Borrower in the principal amount of {Money(d.Amount)}. Borrower's obligation to repay the Loan is evidenced by a promissory note of even date (the \"Note\").", "2.1 The Loan.");
        f.Para($"The unpaid principal balance of the Loan bears interest at {d.RateText}. Interest is calculated on the basis of a 360-day year for the actual number of days elapsed.", "2.2 Interest.");
        f.Para(d.Repayment, "2.3 Repayment.");
        f.Para("Borrower may prepay the Loan in whole or in part at any time upon ten days' written notice to Lender, together with accrued interest on the amount prepaid. Partial prepayments are applied to installments in inverse order of maturity.", "2.4 Prepayment.");
        f.Para("If any payment is not received within ten days after its due date, Borrower shall pay a late charge equal to five percent (5%) of the overdue amount. Upon an Event of Default, the Loan bears interest at a rate five percent (5%) per annum above the otherwise applicable rate.", "2.5 Late Charges; Default Rate.");
        f.Para($"Borrower shall pay Lender an origination fee equal to {d.Fee} of the principal amount of the Loan on the date of this Agreement, and shall reimburse Lender on demand for all reasonable costs and expenses, including attorneys' fees, incurred in connection with the Loan Documents.", "2.6 Fees and Expenses.");

        f.Heading("ARTICLE 3 - SECURITY INTEREST");
        f.Para("As security for the prompt payment and performance of all Obligations, Borrower grants to Lender a continuing security interest in and lien upon all of Borrower's right, title and interest in the following property, wherever located, whether now owned or hereafter acquired (collectively, the \"Collateral\"):", "3.1 Grant of Security Interest.");
        var letter = 'a';
        foreach (var key in d.Collateral) f.Para($"{CollateralKinds[key].Legal};", $"({letter++})", indent: 24, after: 2);
        f.Para("and all proceeds, products, rents and profits of the foregoing, including insurance proceeds and all books and records relating to any of the foregoing.", "(" + letter + ")", indent: 24);
        f.Para($"Borrower authorizes Lender to file one or more financing statements and amendments, in any jurisdiction and with the {d.FilingOffice}, describing the Collateral in any manner Lender considers appropriate, including as \"all assets\". Borrower shall execute any further documents Lender reasonably requests to perfect and maintain the priority of its security interest.", "3.2 Authorization to File; Perfection.");
        f.Para($"The Collateral will be kept at Borrower's place of business at {d.Street}, {d.City}, {d.StateAbbr}, except for vehicles and mobile equipment used in the ordinary course. Borrower shall give Lender thirty days' prior written notice of any change in its name, jurisdiction of organization, or chief executive office.", "3.3 Location of Collateral.");

        f.Heading("ARTICLE 4 - REPRESENTATIONS AND WARRANTIES");
        f.Para($"Borrower is duly organized, validly existing and in good standing under the laws of {d.State}, and has full power and authority to enter into and perform the Loan Documents. The Loan Documents have been duly authorized and are the legal, valid and binding obligations of Borrower.", "4.1 Organization; Authority.");
        f.Para("Borrower owns the Collateral free and clear of all liens other than Permitted Liens. The financial statements delivered to Lender fairly present Borrower's financial condition as of their dates. No litigation or governmental proceeding is pending or, to Borrower's knowledge, threatened that could reasonably be expected to have a material adverse effect.", "4.2 Title; Financial Statements; Litigation.");

        f.Heading("ARTICLE 5 - COVENANTS");
        f.Para("Borrower shall deliver to Lender (a) annual financial statements within 120 days after each fiscal year end, (b) quarterly internal financial statements within 45 days after each fiscal quarter end, and (c) copies of federal income tax returns within 30 days after filing.", "5.1 Financial Reporting.");
        f.Para($"Borrower shall maintain insurance on the Collateral against loss, theft and damage for its full replacement value, naming Lender as lender loss payee, and shall maintain general liability insurance in amounts reasonably satisfactory to Lender.", "5.2 Insurance.");
        f.Para("Borrower shall maintain a debt service coverage ratio of not less than 1.25 to 1.00, tested annually on the basis of Borrower's fiscal year-end financial statements.", "5.3 Financial Covenant.");
        f.Para("Without Lender's prior written consent, Borrower shall not (a) create or permit any lien on the Collateral other than Permitted Liens, (b) sell, lease or otherwise dispose of any Collateral except inventory sold in the ordinary course, (c) merge or consolidate with any other person, or (d) incur additional indebtedness for borrowed money in excess of $100,000 in the aggregate.", "5.4 Negative Covenants.");

        f.Heading("ARTICLE 6 - DEFAULT AND REMEDIES");
        f.Para("Each of the following is an \"Event of Default\": (a) Borrower fails to pay any amount when due and the failure continues for ten days; (b) Borrower breaches any other covenant and the breach is not cured within thirty days after notice; (c) any representation proves to have been materially false when made; (d) Borrower becomes insolvent or commences or is subject to any bankruptcy or insolvency proceeding; or (e) a judgment exceeding $100,000 is entered against Borrower and remains unsatisfied for thirty days.", "6.1 Events of Default.");
        f.Para("Upon an Event of Default, Lender may declare all Obligations immediately due and payable, and may exercise all rights and remedies of a secured party under the UCC, including taking possession of the Collateral, requiring Borrower to assemble the Collateral at a place designated by Lender, collecting accounts directly from account debtors, and selling the Collateral at public or private sale after reasonable notice. Lender's remedies are cumulative.", "6.2 Remedies.");

        f.Heading("ARTICLE 7 - GENERAL PROVISIONS");
        f.Para($"This Agreement is governed by the laws of the State of {d.State}, without regard to conflict of laws principles.", "7.1 Governing Law.");
        f.Para("Notices must be in writing and are effective when delivered by hand or overnight courier, or three days after mailing by certified mail, to the addresses stated in the first paragraph of this Agreement.", "7.2 Notices.");
        f.Para("EACH PARTY WAIVES ITS RIGHT TO A JURY TRIAL IN ANY ACTION ARISING OUT OF THIS AGREEMENT OR THE OTHER LOAN DOCUMENTS.", "7.3 Waiver of Jury Trial.");
        f.Para("This Agreement and the other Loan Documents are the entire agreement of the parties on their subject matter and may be amended only by a writing signed by both parties. This Agreement may be signed in counterparts, each of which is an original.", "7.4 Entire Agreement; Counterparts.");

        // Signature page
        f.NewPage();
        f.Para("IN WITNESS WHEREOF, the parties have caused this Agreement to be executed by their duly authorized representatives as of the date first written above.", after: 24);
        var sig = f.Current;
        var sy = f.Y + 4;
        sig.T(72, sy, "BORROWER:", 10, Face.SerifBold).T(72, sy + 14, d.Borrower, 10, Face.Serif);
        sig.T(72, sy + 46, "By:", 10, Face.Serif).L(92, sy + 50, 330, sy + 50).Sign(98, sy + 24, 150, 26, d.SignerName.Length * 13);
        sig.T(72, sy + 66, $"Name: {d.SignerName}", 10, Face.Serif).T(72, sy + 80, $"Title: {d.SignerTitle}", 10, Face.Serif).T(72, sy + 94, $"Date: {Short(d.AgreementDate)}", 10, Face.Serif);
        sy += 140;
        sig.T(72, sy, "LENDER:", 10, Face.SerifBold).T(72, sy + 14, d.Lender, 10, Face.Serif);
        sig.T(72, sy + 46, "By:", 10, Face.Serif).L(92, sy + 50, 330, sy + 50).Sign(98, sy + 24, 140, 26, d.LenderSigner.Length * 11);
        sig.T(72, sy + 66, $"Name: {d.LenderSigner}", 10, Face.Serif).T(72, sy + 80, $"Title: {d.LenderSignerTitle}", 10, Face.Serif).T(72, sy + 94, $"Date: {Short(d.AgreementDate)}", 10, Face.Serif);

        // Schedule A
        f.NewPage();
        f.Title("SCHEDULE A", 13);
        f.Center("Description of Collateral", 11);
        f.Gap(8);
        f.Para($"In addition to the property described in Section 3.1 of the Loan and Security Agreement dated {Long(d.AgreementDate)}, the Collateral includes the following specific items of Equipment, together with all attachments, accessories and replacements:");
        f.Gap(6);
        var page = f.Current;
        var ty = f.Y + 8;
        page.B(72, ty - 10, 468, 16, 0.88f).T(76, ty + 1, "Description", 9, Face.SerifBold).T(300, ty + 1, "Serial / VIN", 9, Face.SerifBold).T(540 - 4 - Measure("Est. value", 9, Face.SerifBold), ty + 1, "Est. value", 9, Face.SerifBold);
        ty += 6;
        foreach (var (desc, serial, value) in d.Equipment)
        {
            ty += 16;
            page.T(76, ty, desc, 9, Face.Serif).T(300, ty, serial, 9, Face.Serif).T(540 - 4 - Measure(value, 9, Face.Serif), ty, value, 9, Face.Serif).L(72, ty + 4, 540, ty + 4, 0.3f);
        }
        f.Y = ty + 24;
        f.Para($"Location of Equipment: {d.Street}, {d.City}, {d.StateAbbr} {d.Zip}.");

        var pages = f.Pages;
        for (var i = 0; i < pages.Count; i++)
            pages[i].TC(306, 756, $"Loan and Security Agreement - {d.Borrower} - Page {i + 1} of {pages.Count}", 8, Face.Serif);
        return pages;
    }

    sealed class Flow
    {
        const float Left = 72, TextWidth = 468, Top = 72, Bottom = 722;
        public readonly List<Page> Pages = [];
        public Page Current { get; private set; } = null!;
        public float Y { get; set; }

        public Flow() => NewPage();

        public void NewPage() { Current = new Page(); Pages.Add(Current); Y = Top; }
        void Need(float h) { if (Y + h > Bottom) NewPage(); }
        public void Gap(float h) => Y += h;

        public void Title(string text, float size) { Need(size * 1.6f); Current.TC(306, Y + size, text, size, Face.SerifBold); Y += size * 1.6f; }
        public void Center(string text, float size) { Need(size * 1.6f); Current.TC(306, Y + size, text, size, Face.Serif); Y += size * 1.6f; }
        public void Heading(string text) { Need(48); Y += 8; Current.T(Left, Y + 10, text, 10.5f, Face.SerifBold); Y += 19; }

        public void Para(string text, string lead = "", float indent = 0, float size = 10, float after = 6)
        {
            var leadWidth = lead.Length > 0 ? Measure(lead + " ", size, Face.SerifBold) : 0;
            var lines = Wrap(text, size, Face.Serif, TextWidth - indent, leadWidth);
            var leading = size * 1.28f;
            for (var i = 0; i < lines.Count; i++)
            {
                Need(leading);
                if (i == 0 && lead.Length > 0) Current.T(Left + indent, Y + size, lead, size, Face.SerifBold);
                Current.T(Left + indent + (i == 0 ? leadWidth : 0), Y + size, lines[i], size, Face.Serif);
                Y += leading;
            }
            Y += after;
        }
    }

    // ---- PDF writer ----------------------------------------------------------------------------------------------

    static string N(float v) => v.ToString("0.##");
    static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);
    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    sealed class PdfBuilder
    {
        readonly List<byte[]> objects = [];
        readonly List<int> pageIds = [];
        readonly List<int> fieldIds = [];
        bool hasFields;

        public PdfBuilder()
        {
            Reserve(); // 1 catalog
            Reserve(); // 2 pages
            foreach (var font in BaseFonts) Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{font} /Encoding /WinAnsiEncoding >>");
        }

        int Reserve() { objects.Add([]); return objects.Count; }
        int Add(string body) { objects.Add(Latin1(body)); return objects.Count; }
        int Add(byte[] body) { objects.Add(body); return objects.Count; }
        void Set(int id, string body) => objects[id - 1] = Latin1(body);

        static byte[] Stream(string dict, byte[] data) =>
            [.. Latin1($"<< {dict} /Length {data.Length} >>\nstream\n"), .. data, .. Latin1("\nendstream")];

        static string FontResources() => string.Join(" ", Enumerable.Range(0, BaseFonts.Length).Select(i => $"/F{i} {3 + i} 0 R"));

        public void AddVectorPage(Page page, bool asForm)
        {
            var pageId = Reserve();
            var annots = new List<int>();
            var content = new StringBuilder();
            foreach (var op in page.Ops)
            {
                switch (op)
                {
                    case TextOp t:
                        content.Append($"BT /F{(int)t.Face} {N(t.Size)} Tf {N(t.X)} {N(Height - t.Y)} Td ({Esc(t.Text)}) Tj ET\n");
                        break;
                    case LineOp l:
                        content.Append($"{N(l.Width)} w {N(l.X1)} {N(Height - l.Y1)} m {N(l.X2)} {N(Height - l.Y2)} l S\n");
                        break;
                    case BoxOp b:
                        if (b.Fill >= 0) content.Append($"{N(b.Fill)} g {N(b.X)} {N(Height - b.Y - b.H)} {N(b.W)} {N(b.H)} re f 0 g\n");
                        content.Append($"0.6 w {N(b.X)} {N(Height - b.Y - b.H)} {N(b.W)} {N(b.H)} re S\n");
                        break;
                    case CheckOp c:
                        content.Append($"0.6 w {N(c.X)} {N(Height - c.Y - c.Size)} {N(c.Size)} {N(c.Size)} re S\n");
                        if (asForm) annots.Add(AddCheckbox(c, pageId));
                        else if (c.On) content.Append(CheckMark(c));
                        break;
                    case FieldOp f:
                        if (asForm) annots.Add(AddTextField(f, pageId));
                        else
                        {
                            var lines = f.Multiline ? FieldLines(f) : [f.Value];
                            for (var i = 0; i < lines.Count; i++)
                            {
                                var baseline = f.Multiline ? f.Y + f.Size + 2 + i * f.Size * 1.25f : f.Y + f.H - 4.5f;
                                if (lines[i].Length > 0) content.Append($"BT /F5 {N(f.Size)} Tf {N(f.X + 3)} {N(Height - baseline)} Td ({Esc(lines[i])}) Tj ET\n");
                            }
                        }
                        break;
                    case SquiggleOp s:
                        var pts = SquigglePoints(s);
                        content.Append($"1.1 w 1 J 1 j {N(pts[0].X)} {N(Height - pts[0].Y)} m ");
                        foreach (var pt in pts.Skip(1)) content.Append($"{N(pt.X)} {N(Height - pt.Y)} l ");
                        content.Append("S 0 J 0 j\n");
                        break;
                    case StampOp st:
                        var rad = st.Angle * Math.PI / 180;
                        content.Append($"q {N((float)Math.Cos(rad))} {N((float)Math.Sin(rad))} {N((float)-Math.Sin(rad))} {N((float)Math.Cos(rad))} {N(st.X)} {N(Height - st.Y)} cm 1.2 w 0 {N(-st.H)} {N(st.W)} {N(st.H)} re S\n");
                        for (var i = 0; i < st.Lines.Length; i++)
                            content.Append($"BT /F{(i == 0 ? 1 : 0)} {(i == 0 ? 11 : 7.5f)} Tf 5 {N(-12 - i * 11)} Td ({Esc(st.Lines[i])}) Tj ET\n");
                        content.Append("Q\n");
                        break;
                }
            }

            var contentId = Add(Stream("", Latin1(content.ToString())));
            var annotArray = annots.Count > 0 ? $" /Annots [{string.Join(" ", annots.Select(a => $"{a} 0 R"))}]" : "";
            Set(pageId, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentId} 0 R /Resources << /Font << {FontResources()} >> >>{annotArray} >>");
            pageIds.Add(pageId);
        }

        static string CheckMark(CheckOp c) =>
            $"1.4 w {N(c.X + 1.5f)} {N(Height - c.Y - 1.5f)} m {N(c.X + c.Size - 1.5f)} {N(Height - c.Y - c.Size + 1.5f)} l S {N(c.X + 1.5f)} {N(Height - c.Y - c.Size + 1.5f)} m {N(c.X + c.Size - 1.5f)} {N(Height - c.Y - 1.5f)} l S\n";

        int AddTextField(FieldOp f, int pageId)
        {
            hasFields = true;
            var lines = f.Multiline ? FieldLines(f) : [f.Value];
            var ap = new StringBuilder("/Tx BMC q BT ");
            ap.Append($"/F5 {N(f.Size)} Tf 0 g ");
            if (f.Multiline) ap.Append($"{N(f.Size * 1.25f)} TL 3 {N(f.H - f.Size - 2)} Td ");
            else ap.Append($"3 {N(4.5f)} Td ");
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) ap.Append("T* ");
                ap.Append($"({Esc(lines[i])}) Tj ");
            }
            ap.Append("ET Q EMC");
            var apId = Add(Stream($"/Type /XObject /Subtype /Form /BBox [0 0 {N(f.W)} {N(f.H)}] /Resources << /Font << /F5 8 0 R >> >>", Latin1(ap.ToString())));
            var value = f.Value.Length > 0 ? $" /V ({Esc(f.Value.Replace('\n', '\r'))})" : "";
            var flags = f.Multiline ? " /Ff 4096" : "";
            var id = Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({Esc(f.Name)}){value}{flags} /Rect [{N(f.X)} {N(Height - f.Y - f.H)} {N(f.X + f.W)} {N(Height - f.Y)}] /P {pageId} 0 R /F 4 /DA (/F5 {N(f.Size)} Tf 0 g) /AP << /N {apId} 0 R >> >>");
            fieldIds.Add(id);
            return id;
        }

        int AddCheckbox(CheckOp c, int pageId)
        {
            hasFields = true;
            var box = $"/Type /XObject /Subtype /Form /BBox [0 0 {N(c.Size)} {N(c.Size)}]";
            var on = Add(Stream(box, Latin1($"1.4 w 1.5 1.5 m {N(c.Size - 1.5f)} {N(c.Size - 1.5f)} l S 1.5 {N(c.Size - 1.5f)} m {N(c.Size - 1.5f)} 1.5 l S")));
            var off = Add(Stream(box, []));
            var state = c.On ? "Yes" : "Off";
            var id = Add($"<< /Type /Annot /Subtype /Widget /FT /Btn /T ({Esc(c.Name)}) /V /{state} /AS /{state} /Rect [{N(c.X)} {N(Height - c.Y - c.Size)} {N(c.X + c.Size)} {N(Height - c.Y)}] /P {pageId} 0 R /F 4 /AP << /N << /Yes {on} 0 R /Off {off} 0 R >> >> >>");
            fieldIds.Add(id);
            return id;
        }

        public void AddJpegPage(byte[] jpeg, int w, int h)
        {
            var image = Add(Stream($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg));
            var content = Add(Stream("", Latin1("q 612 0 0 792 0 0 cm /Im0 Do Q")));
            pageIds.Add(Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R /Resources << /XObject << /Im0 {image} 0 R >> >> >>"));
        }

        public byte[] Build(string title)
        {
            var acro = hasFields
                ? $" /AcroForm << /Fields [{string.Join(" ", fieldIds.Select(i => $"{i} 0 R"))}] /DA (/F5 9 Tf 0 g) /DR << /Font << /F5 8 0 R >> >> >>"
                : "";
            Set(1, $"<< /Type /Catalog /Pages 2 0 R{acro} >>");
            Set(2, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(i => $"{i} 0 R"))}] /Count {pageIds.Count} >>");
            var info = Add($"<< /Title ({Esc(title)}) /Producer (LayaSample corpus generator) /Subject (Synthetic sample document; all parties and data are fictional) >>");

            using var ms = new MemoryStream();
            void Write(byte[] b) => ms.Write(b);
            Write(Latin1("%PDF-1.7\n%âãÏÓ\n"));
            var offsets = new long[objects.Count];
            for (var i = 0; i < objects.Count; i++)
            {
                offsets[i] = ms.Position;
                Write(Latin1($"{i + 1} 0 obj\n"));
                Write(objects[i]);
                Write(Latin1("\nendobj\n"));
            }
            var xref = ms.Position;
            var sb = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
            sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Write(Latin1(sb.ToString()));
            return ms.ToArray();
        }
    }

    static byte[] BuildVector(List<Page> pages, bool asForm, string title)
    {
        var pdf = new PdfBuilder();
        foreach (var page in pages) pdf.AddVectorPage(page, asForm);
        return pdf.Build(title);
    }

    // ---- rasterizing and degrading -------------------------------------------------------------------------------

    static SKPoint[] SquigglePoints(SquiggleOp s)
    {
        var rng = new Random(s.Seed);
        const int n = 28;
        var pts = new SKPoint[n];
        var phase = rng.NextDouble() * 6;
        for (var i = 0; i < n; i++)
        {
            var t = i / (float)(n - 1);
            var amp = 0.25 + 0.75 * Math.Sin(Math.PI * t); // taller in the middle
            var yy = s.Y + s.H / 2 + (float)(Math.Sin(phase + t * 17) * amp * s.H * 0.38 + (rng.NextDouble() - 0.5) * s.H * 0.18);
            pts[i] = new SKPoint(s.X + t * s.W, yy);
        }
        return pts;
    }

    static SKBitmap Raster(Page page, float dpi)
    {
        var scale = dpi / 72f;
        var bitmap = new SKBitmap((int)(Width * scale), (int)(Height * scale));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        foreach (var op in page.Ops)
        {
            switch (op)
            {
                case TextOp t:
                    DrawText(canvas, paint, t.Text, t.X, t.Y, t.Size, t.Face);
                    break;
                case LineOp l:
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = l.Width;
                    canvas.DrawLine(l.X1, l.Y1, l.X2, l.Y2, paint);
                    break;
                case BoxOp b:
                    if (b.Fill >= 0)
                    {
                        paint.Style = SKPaintStyle.Fill; paint.Color = new SKColor((byte)(b.Fill * 255), (byte)(b.Fill * 255), (byte)(b.Fill * 255));
                        canvas.DrawRect(b.X, b.Y, b.W, b.H, paint);
                        paint.Color = SKColors.Black;
                    }
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 0.6f;
                    canvas.DrawRect(b.X, b.Y, b.W, b.H, paint);
                    break;
                case CheckOp c:
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 0.6f;
                    canvas.DrawRect(c.X, c.Y, c.Size, c.Size, paint);
                    if (c.On)
                    {
                        paint.StrokeWidth = 1.4f;
                        canvas.DrawLine(c.X + 1.5f, c.Y + 1.5f, c.X + c.Size - 1.5f, c.Y + c.Size - 1.5f, paint);
                        canvas.DrawLine(c.X + 1.5f, c.Y + c.Size - 1.5f, c.X + c.Size - 1.5f, c.Y + 1.5f, paint);
                    }
                    break;
                case FieldOp f:
                    var lines = f.Multiline ? FieldLines(f) : [f.Value];
                    for (var i = 0; i < lines.Count; i++)
                        DrawText(canvas, paint, lines[i], f.X + 3, f.Multiline ? f.Y + f.Size + 2 + i * f.Size * 1.25f : f.Y + f.H - 4.5f, f.Size, Face.Mono);
                    break;
                case SquiggleOp s:
                    var pts = SquigglePoints(s);
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 1.1f;
                    using (var path = new SKPath())
                    {
                        path.MoveTo(pts[0].X, pts[0].Y);
                        foreach (var pt in pts.Skip(1)) path.LineTo(pt.X, pt.Y);
                        canvas.DrawPath(path, paint);
                    }
                    break;
                case StampOp st:
                    canvas.Save();
                    canvas.Translate(st.X, st.Y);
                    canvas.RotateDegrees(-st.Angle);
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 1.2f;
                    canvas.DrawRect(0, -st.H, st.W, st.H, paint);
                    for (var i = 0; i < st.Lines.Length; i++)
                        DrawText(canvas, paint, st.Lines[i], 5, -st.H + 12 + i * 11, i == 0 ? 11 : 7.5f, i == 0 ? Face.SansBold : Face.Sans);
                    canvas.Restore();
                    break;
            }
        }
        return bitmap;
    }

    static void DrawText(SKCanvas canvas, SKPaint paint, string text, float x, float y, float size, Face face)
    {
        if (text.Length == 0) return;
        paint.Style = SKPaintStyle.Fill;
        using var font = new SKFont(Typefaces[face], size);
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
    }

    static byte[] Png(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static byte[] Jpeg(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    // Office scanner: slightly rotated, blurred, off-white paper, shadow along the binding edge, dust and a streak.
    static SKBitmap Scan(SKBitmap source, Random rng)
    {
        var w = source.Width;
        var h = source.Height;
        var result = new SKBitmap(w, h);
        using var canvas = new SKCanvas(result);
        canvas.Clear(new SKColor(247, 245, 238));
        canvas.Save();
        canvas.Translate(w / 2f + (float)(rng.NextDouble() * 8 - 4), h / 2f + (float)(rng.NextDouble() * 8 - 4));
        canvas.RotateDegrees((float)(rng.NextDouble() * 1.4 - 0.7));
        canvas.Translate(-w / 2f, -h / 2f);
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Multiply, ImageFilter = SKImageFilter.CreateBlur(0.7f, 0.7f), IsAntialias = true })
            canvas.DrawBitmap(source, 0, 0, paint);
        canvas.Restore();

        using (var shade = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w * 0.07f, 0), [new SKColor(0, 0, 0, 70), SKColors.Transparent], SKShaderTileMode.Clamp) })
            canvas.DrawRect(0, 0, w * 0.07f, h, shade);
        using (var dust = new SKPaint { Color = new SKColor(0, 0, 0), IsAntialias = true })
        {
            for (var i = 0; i < 90; i++)
            {
                dust.Color = new SKColor(40, 40, 40, (byte)rng.Next(60, 190));
                canvas.DrawCircle(rng.Next(w), rng.Next(h), (float)(0.4 + rng.NextDouble() * 1.1), dust);
            }
            dust.Color = new SKColor(0, 0, 0, 22);
            dust.StrokeWidth = 1.2f;
            var streak = rng.Next((int)(w * 0.2), (int)(w * 0.9));
            canvas.DrawLine(streak, 0, streak + rng.Next(-3, 4), h, dust);
        }
        return result;
    }

    static byte[] BuildScanned(List<Page> pages, int firstScanned, Random rng, string title)
    {
        const int dpi = 150;
        var pdf = new PdfBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            if (i < firstScanned) { pdf.AddVectorPage(pages[i], false); continue; }
            using var clean = Raster(pages[i], dpi);
            using var scan = Scan(clean, rng);
            pdf.AddJpegPage(Jpeg(scan, 62), scan.Width, scan.Height);
        }
        return pdf.Build(title);
    }

    // Group 4 fax at 204 dpi: standard resolution is 98 lines/inch vertically, fine is 196. Each page carries the
    // sending machine's header line, a little skew, dropped scan lines and speckle.
    static byte[] BuildFax(List<Page> pages, Random rng, DateOnly date, string sender, string phone, int verticalDpi)
    {
        using var collection = new MagickImageCollection();
        for (var i = 0; i < pages.Count; i++)
        {
            using var clean = Raster(pages[i], 204);
            var w = clean.Width;
            var h = clean.Height;
            using var faxed = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(faxed))
            {
                canvas.Clear(SKColors.White);
                canvas.Save();
                canvas.Translate(w / 2f, h / 2f);
                canvas.RotateDegrees((float)(rng.NextDouble() * 0.8 - 0.4));
                canvas.Translate(-w / 2f, -h / 2f);
                using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(0.9f, 0.9f), IsAntialias = true })
                    canvas.DrawBitmap(clean, 0, 0, paint);
                canvas.Restore();

                using var noise = new SKPaint { Color = SKColors.Black, IsAntialias = false };
                for (var k = 0; k < 140; k++) canvas.DrawCircle(rng.Next(w), rng.Next(h), (float)(0.6 + rng.NextDouble() * 1.4), noise);
                noise.Color = SKColors.White;
                for (var k = 0; k < 3; k++) canvas.DrawRect(0, rng.Next(h), w, (float)(1 + rng.NextDouble() * 2), noise);

                var header = $"{date:MMM dd yyyy}  03:41PM  FROM: {sender.ToUpperInvariant()}  TEL: {phone}  P.{i + 1}/{pages.Count}";
                var scale = 204 / 72f;
                using var font = new SKFont(Typefaces[Face.Mono], 7 * scale);
                canvas.DrawRect(0, 0, w, 20 * scale, new SKPaint { Color = SKColors.White });
                using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                canvas.DrawText(header, 36 * scale, 14 * scale, SKTextAlign.Left, font, text);
            }

            var image = new MagickImage(Png(faxed));
            image.Alpha(AlphaOption.Off);
            image.Resize(new MagickGeometry(1728, (uint)(11 * verticalDpi)) { IgnoreAspectRatio = true });
            image.Grayscale();
            image.Threshold(new Percentage(66));
            image.ColorType = ColorType.Bilevel;
            image.Density = new Density(204, verticalDpi, DensityUnit.PixelsPerInch);
            image.Settings.Compression = CompressionMethod.Group4;
            collection.Add(image);
        }
        return collection.ToByteArray(MagickFormat.Pdf);
    }
}
