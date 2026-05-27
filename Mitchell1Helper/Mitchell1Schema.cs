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
