namespace Capture.Core.Indexing;

/// <summary>A ready-made document type for AI separation: what it looks like (<see cref="Description"/>,
/// which the AI reads) and the AI field catalog types its documents usually carry.</summary>
public sealed record AiDocumentType(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> FieldTypeIds,
    bool StartsNewDocument = true);

public static class AiDocumentTypeCatalog
{
    public static readonly IReadOnlyList<AiDocumentType> DefaultTypes =
    [
        new("invoice", "Invoice",
            "A supplier's bill requesting payment for goods or services, with an invoice number, charges or line items, tax and a total due. A remittance slip printed at the bottom is still part of the invoice.",
            ["invoice.number", "invoice.supplier", "invoice.abn", "invoice.date", "invoice.due", "invoice.po", "invoice.net", "invoice.tax", "invoice.total"]),
        new("credit-note", "Credit Note",
            "A supplier's credit note or adjustment note that reverses or reduces an earlier invoice, usually quoting the original invoice number.",
            ["credit.number", "credit.invoice", "credit.supplier", "credit.date", "credit.total"]),
        new("statement", "Statement",
            "An account statement listing a customer's invoices, credits and payments over a period, with an opening or closing balance owing. Not a bank statement.",
            ["statement.supplier", "statement.account", "statement.date", "statement.closing"]),
        new("purchase-order", "Purchase Order",
            "A buyer's formal order to a supplier for goods or services, with a PO number, ordered items and delivery details.",
            ["po.number", "po.vendor", "po.date", "po.total"]),
        new("remittance-advice", "Remittance Advice",
            "A standalone notice from a payer confirming a payment and which invoices it covers. Only a page that is entirely a payment notice, with no invoice charges.",
            ["remittance.payer", "remittance.date", "remittance.amount", "remittance.reference"]),
        new("delivery-docket", "Delivery Docket",
            "A delivery docket, packing slip or goods received note listing items delivered, usually without prices.",
            ["delivery.number", "delivery.date", "delivery.shipto"]),
        new("receipt", "Receipt",
            "Proof of payment for a completed purchase, such as a till or card receipt.",
            ["receipt.merchant", "receipt.number", "receipt.date", "receipt.amount"]),
        new("contract", "Contract",
            "A legal agreement between parties, with terms and conditions and usually signatures.",
            ["contract.number", "contract.party", "contract.counterparty", "contract.start", "contract.end", "contract.value"]),
        new("correspondence", "Correspondence",
            "A letter, memo or email printout — general written communication addressed to someone.",
            ["letter.sender", "letter.recipient", "letter.date", "letter.subject", "letter.reference"])
    ];

    private static IReadOnlyList<AiDocumentType> _all = DefaultTypes;

    public static IReadOnlyList<AiDocumentType> All => _all;

    public static AiDocumentType? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _all.FirstOrDefault(item => item.Id == id);

    /// <summary>Replaces the in-memory catalog, e.g. after loading a user-edited JSON file.</summary>
    public static void Load(IReadOnlyList<AiDocumentType>? types) =>
        _all = types is { Count: > 0 } ? types : DefaultTypes;
}

public interface IAiDocumentTypeStore
{
    /// <summary>Loads the AI document type catalog, creating it from
    /// <see cref="AiDocumentTypeCatalog.DefaultTypes"/> on first run.</summary>
    Task<IReadOnlyList<AiDocumentType>> LoadAsync(CancellationToken cancellationToken = default);
}
