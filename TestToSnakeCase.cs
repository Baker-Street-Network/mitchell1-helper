// Quick test to verify ToSnakeCase conversion
// Paste this into a C# REPL or test project to verify

using System;
using System.Text;

string ToSnakeCase(string pascalCase)
{
    if (string.IsNullOrEmpty(pascalCase))
        return pascalCase;

    var result = new StringBuilder();

    for (int i = 0; i < pascalCase.Length; i++)
    {
        char current = pascalCase[i];
        bool isUpper = char.IsUpper(current);

        // Add underscore before uppercase letter if:
        // 1. Not the first character
        // 2. Previous character is lowercase OR next character is lowercase (end of acronym)
        if (i > 0 && isUpper)
        {
            char prev = pascalCase[i - 1];
            bool prevIsLower = char.IsLower(prev);

            // Check if this is the end of an acronym (e.g., "POMaster" -> M is end of "PO")
            bool endOfAcronym = false;
            if (i + 1 < pascalCase.Length)
            {
                char next = pascalCase[i + 1];
                endOfAcronym = char.IsUpper(prev) && char.IsLower(next);
            }

            if (prevIsLower || endOfAcronym)
            {
                result.Append('_');
            }
        }

        result.Append(char.ToLowerInvariant(current));
    }

    return result.ToString();
}

// Test cases
var testCases = new[]
{
    ("POMasterId", "po_master_id"),
    ("PODetailId", "po_detail_id"),
    ("PODate", "po_date"),
    ("POShipViaId", "po_ship_via_id"),
    ("POStateId", "po_state_id"),
    ("LastChangeDate", "last_change_date"),
    ("RecordVersion", "record_version"),
    ("WriterId", "writer_id"),
    ("VendorId", "vendor_id"),
    ("CommentInternal", "comment_internal"),
    ("IsClosed", "is_closed"),
    ("TaxTotal", "tax_total"),
    ("BackOrder", "back_order"),
    ("PhoneExt", "phone_ext"),
    ("ConfirmNum", "confirm_num"),
    ("TrackingNumber", "tracking_number"),
    ("InventoryPart", "inventory_part"),
    ("PartNumber", "part_number"),
    ("QuantityReceived", "quantity_received"),
    ("QuantityBackOrder", "quantity_back_order"),
    ("QuantityCancelled", "quantity_cancelled"),
    ("CoreCost", "core_cost"),
    ("MfgId", "mfg_id"),
    ("AltPartNo", "alt_part_no"),
    ("AltMfgId", "alt_mfg_id"),
    ("RepairOrderId", "repair_order_id"),
    ("WDLineCode", "wd_line_code"),
    ("LocationId", "location_id"),
    ("IsTire", "is_tire"),
    ("CatalogVendorId", "catalog_vendor_id"),
    ("AddedFromSupplier", "added_from_supplier"),
    ("PurchasedFromVendor", "purchased_from_vendor"),
    ("ShippingCost", "shipping_cost"),
};

Console.WriteLine("Testing ToSnakeCase conversion:");
Console.WriteLine("================================");

int passed = 0;
int failed = 0;

foreach (var (input, expected) in testCases)
{
    string actual = ToSnakeCase(input);
    bool success = actual == expected;

    if (success)
    {
        passed++;
        Console.WriteLine($"✓ {input,-25} -> {actual}");
    }
    else
    {
        failed++;
        Console.WriteLine($"✗ {input,-25} -> {actual} (expected: {expected})");
    }
}

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed");
