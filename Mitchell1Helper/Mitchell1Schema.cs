namespace Mitchell1Helper;

/// <summary>
/// Static schema definition for Mitchell1 SQL Server database.
/// These values are constants specific to the Mitchell1 database structure.
/// </summary>
public static class Mitchell1Schema
{
    /// <summary>
    /// Database schema name
    /// </summary>
    public const string SchemaName = "SM";

    /// <summary>
    /// Customers table name (without schema)
    /// </summary>
    public const string CustomersTableName = "Customers";

    /// <summary>
    /// Customers table full name (schema.table)
    /// </summary>
    public const string CustomersTable = $"{SchemaName}.{CustomersTableName}";

    /// <summary>
    /// Customer ID column name (primary key)
    /// </summary>
    public const string IdColumn = "CustId";

    /// <summary>
    /// Last change date column name (datetime2)
    /// </summary>
    public const string LastChangeDateColumn = "LastChangeDate";

    /// <summary>
    /// Is deleted flag column name
    /// </summary>
    public const string IsDeletedColumn = "IsDeleted";
    /// <summary>
    /// Is deleted flag column name
    /// </summary>
    public const string DeletedColumn = "Deleted";

    // Vendor Schema Constants

    /// <summary>
    /// Vendors table name (without schema)
    /// </summary>
    public const string VendorsTableName = "Vendor";

    /// <summary>
    /// Vendors table full name (schema.table)
    /// </summary>
    public const string VendorsTable = $"{SchemaName}.{VendorsTableName}";

    /// <summary>
    /// Vendor ID column name (primary key)
    /// </summary>
    public const string VendorIdColumn = "VendorId";

    // Parts Schema Constants

    /// <summary>
    /// Parts table name (without schema)
    /// </summary>
    public const string PartsTableName = "Part";

    /// <summary>
    /// Parts table full name (schema.table)
    /// </summary>
    public const string PartsTable = $"{SchemaName}.{PartsTableName}";

    /// <summary>
    /// Part ID column name (primary key)
    /// </summary>
    public const string PartIdColumn = "PartId";

    // Repair Order Schema Constants

    /// <summary>
    /// RepairOrder table name (without schema)
    /// </summary>
    public const string RepairOrderTableName = "RepairOrder";

    /// <summary>
    /// RepairOrder table full name (schema.table)
    /// </summary>
    public const string RepairOrderTable = $"{SchemaName}.{RepairOrderTableName}";

    /// <summary>
    /// Repair Order ID column name (primary key)
    /// </summary>
    public const string RepairOrderIdColumn = "RepairOrderId";

    /// <summary>
    /// LineItem table name (without schema)
    /// </summary>
    public const string LineItemTableName = "LineItem";

    /// <summary>
    /// LineItem table full name (schema.table)
    /// </summary>
    public const string LineItemTable = $"{SchemaName}.{LineItemTableName}";

    /// <summary>
    /// Line Item ID column name (primary key)
    /// </summary>
    public const string LineItemIdColumn = "LineItemId";

    /// <summary>
    /// RepairOrderLineItemSequence table name (without schema)
    /// </summary>
    public const string RepairOrderLineItemSequenceTableName = "RepairOrderLineItemSequence";

    /// <summary>
    /// RepairOrderLineItemSequence table full name (schema.table)
    /// </summary>
    public const string RepairOrderLineItemSequenceTable = $"{SchemaName}.{RepairOrderLineItemSequenceTableName}";

    // Purchase Order Schema Constants

    /// <summary>
    /// POMaster table name (without schema)
    /// </summary>
    public const string POMasterTableName = "POMaster";

    /// <summary>
    /// POMaster table full name (schema.table)
    /// </summary>
    public const string POMasterTable = $"{SchemaName}.{POMasterTableName}";

    /// <summary>
    /// PODetail table name (without schema)
    /// </summary>
    public const string PODetailTableName = "PODetail";

    /// <summary>
    /// PODetail table full name (schema.table)
    /// </summary>
    public const string PODetailTable = $"{SchemaName}.{PODetailTableName}";

    /// <summary>
    /// PO Master ID column name (primary key)
    /// </summary>
    public const string POMasterIdColumn = "POMasterId";

    /// <summary>
    /// PO Detail ID column name (primary key)
    /// </summary>
    public const string PODetailIdColumn = "PODetailId";

    // GL Account Schema Constants

    /// <summary>
    /// GLAccounts table name (without schema)
    /// </summary>
    public const string GLAccountsTableName = "GLAccounts";

    /// <summary>
    /// GLAccounts table full name (schema.table)
    /// </summary>
    public const string GLAccountsTable = $"{SchemaName}.{GLAccountsTableName}";

    /// <summary>
    /// GL Account ID column name (primary key)
    /// </summary>
    public const string GLAccountIdColumn = "GLAccountId";

    /// <summary>
    /// GLEntryTypes table name (without schema)
    /// </summary>
    public const string GLEntryTypesTableName = "GLEntryTypes";

    /// <summary>
    /// GLEntryTypes table full name (schema.table)
    /// </summary>
    public const string GLEntryTypesTable = $"{SchemaName}.{GLEntryTypesTableName}";

    /// <summary>
    /// Minimum valid SQL Server datetime value (for datetime/datetime2 compatibility)
    /// </summary>
    public static readonly DateTime MinValidSqlDateTime = new(1753, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Maximum valid SQL Server datetime value
    /// </summary>
    public static readonly DateTime MaxValidSqlDateTime = new(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    /// <summary>
    /// Checks if a DateTime value is within the valid SQL Server datetime range
    /// </summary>
    public static bool IsValidSqlDateTime(DateTime value)
    {
        return value >= MinValidSqlDateTime && value <= MaxValidSqlDateTime;
    }

    /// <summary>
    /// Clamps a DateTime value to the valid SQL Server datetime range
    /// </summary>
    public static DateTime ClampToValidRange(DateTime value)
    {
        if (value < MinValidSqlDateTime)
            return MinValidSqlDateTime;
        if (value > MaxValidSqlDateTime)
            return MaxValidSqlDateTime;
        return value;
    }
}
