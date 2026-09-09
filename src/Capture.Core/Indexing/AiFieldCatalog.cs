using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public sealed record AiFieldType(string Id, string Classification, string Name, FieldFormat Format, string Hint);

public static class AiFieldCatalog
{
    public const string CustomTypeId = "custom.field";

    public static readonly IReadOnlyList<AiFieldType> DefaultTypes =
    [
        Type(CustomTypeId, "Custom", "Custom Field", FieldFormat.String, ""),
        Type("invoice.number", "Invoice", "Invoice No", FieldFormat.String, "Supplier-facing invoice number, not a PO or customer reference."),
        Type("invoice.supplier", "Invoice", "Supplier Name", FieldFormat.String, "Vendor or supplier who issued the invoice."),
        Type("invoice.customer", "Invoice", "Customer Name", FieldFormat.String, "Bill-to customer or account name."),
        Type("invoice.date", "Invoice", "Invoice Date", FieldFormat.Date, "Date the invoice was issued."),
        Type("invoice.due", "Invoice", "Due Date", FieldFormat.Date, "Payment due date."),
        Type("invoice.total", "Invoice", "Invoice Total", FieldFormat.Money, "Grand total including tax."),
        Type("invoice.tax", "Invoice", "Tax Amount", FieldFormat.Money, "Total tax / VAT / GST."),
        Type("invoice.currency", "Invoice", "Currency", FieldFormat.String, "ISO currency code or symbol, e.g. AUD or $."),
        Type("invoice.po", "Invoice", "Purchase Order No", FieldFormat.String, "Customer PO number referenced on the invoice."),
        Type("contract.number", "Contract", "Contract No", FieldFormat.String, "Agreement or contract identifier."),
        Type("contract.party", "Contract", "Party Name", FieldFormat.String, "Primary contracting party."),
        Type("contract.counterparty", "Contract", "Counterparty", FieldFormat.String, "The other contracting party."),
        Type("contract.start", "Contract", "Start Date", FieldFormat.Date, "Effective or commencement date."),
        Type("contract.end", "Contract", "End Date", FieldFormat.Date, "Expiry or termination date."),
        Type("contract.value", "Contract", "Contract Value", FieldFormat.Money, "Total contract value or consideration."),
        Type("contract.renewal", "Contract", "Renewal Date", FieldFormat.Date, "Next renewal or review date."),
        Type("po.number", "Purchase Order", "PO Number", FieldFormat.String, "Purchase order number."),
        Type("po.vendor", "Purchase Order", "Vendor", FieldFormat.String, "Supplier the PO is issued to."),
        Type("po.date", "Purchase Order", "Order Date", FieldFormat.Date, "Date the purchase order was raised."),
        Type("po.total", "Purchase Order", "PO Total", FieldFormat.Money, "Total PO amount."),
        Type("delivery.number", "Delivery", "Delivery Note No", FieldFormat.String, "Delivery docket or goods-received number."),
        Type("delivery.date", "Delivery", "Delivery Date", FieldFormat.Date, "Date goods were delivered or shipped."),
        Type("delivery.shipto", "Delivery", "Ship To", FieldFormat.String, "Delivery address or site."),
        Type("receipt.number", "Receipt", "Receipt No", FieldFormat.String, "Receipt or transaction number."),
        Type("receipt.merchant", "Receipt", "Merchant", FieldFormat.String, "Store or merchant name."),
        Type("receipt.date", "Receipt", "Receipt Date", FieldFormat.Date, "Date of the purchase."),
        Type("receipt.amount", "Receipt", "Amount", FieldFormat.Money, "Receipt total."),
        Type("letter.reference", "Correspondence", "Reference", FieldFormat.String, "Letter or file reference."),
        Type("letter.sender", "Correspondence", "Sender", FieldFormat.String, "From / author."),
        Type("letter.recipient", "Correspondence", "Recipient", FieldFormat.String, "Addressee."),
        Type("letter.date", "Correspondence", "Letter Date", FieldFormat.Date, "Date on the letter."),
        Type("letter.subject", "Correspondence", "Subject", FieldFormat.String, "Subject or re: line."),
        Type("general.number", "General", "Document Number", FieldFormat.String, "Primary document identifier."),
        Type("general.date", "General", "Document Date", FieldFormat.Date, "Primary date on the document."),
        Type("general.amount", "General", "Amount", FieldFormat.Money, "Primary monetary amount."),
        Type("general.name", "General", "Name", FieldFormat.String, "Primary person or organisation name.")
    ];

    private static IReadOnlyList<AiFieldType> _all = DefaultTypes;

    public static IReadOnlyList<AiFieldType> All => _all;

    public static IReadOnlyList<string> Classifications =>
        _all.Select(item => item.Classification).Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<AiFieldType> ForClassification(string? classification) =>
        _all.Where(item => string.Equals(item.Classification, classification, StringComparison.Ordinal)).ToList();

    public static AiFieldType? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _all.FirstOrDefault(item => item.Id == id);

    /// <summary>Replaces the in-memory catalog, e.g. after loading a user-edited JSON file. Always keeps a Custom entry.</summary>
    public static void Load(IReadOnlyList<AiFieldType>? types)
    {
        if (types is null || types.Count == 0)
        {
            _all = DefaultTypes;
            return;
        }

        _all = types.Any(item => item.Id == CustomTypeId)
            ? types
            : [DefaultTypes.First(item => item.Id == CustomTypeId), .. types];
    }

    private static AiFieldType Type(string id, string classification, string name, FieldFormat format, string hint) =>
        new(id, classification, name, format, hint);
}
