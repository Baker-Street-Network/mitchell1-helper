namespace Mitchell1Helper.Services;

public class SyncState
{
    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mitchell1Helper",
        "sync-state.json"
    );

    private readonly object _lock = new();

    public DateTime? LastSuccessfulPollUtc { get; private set; }
    public DateTime? LastProcessedChangeDateUtc { get; private set; }
    public long? LastProcessedCustomerId { get; private set; }
    public string? LastError { get; private set; }
    public int LastBatchProcessedCount { get; private set; }

    // Vendor sync state
    public DateTime? LastSuccessfulVendorPollUtc { get; private set; }
    public DateTime? LastProcessedVendorChangeDateUtc { get; private set; }
    public long? LastProcessedVendorId { get; private set; }
    public string? LastVendorError { get; private set; }
    public int LastVendorBatchProcessedCount { get; private set; }

    // Part sync state
    public DateTime? LastSuccessfulPartPollUtc { get; private set; }
    public DateTime? LastProcessedPartChangeDateUtc { get; private set; }
    public long? LastProcessedPartId { get; private set; }
    public string? LastPartError { get; private set; }
    public int LastPartBatchProcessedCount { get; private set; }

    // Repair Order sync state
    public DateTime? LastSuccessfulRepairOrderPollUtc { get; private set; }
    public DateTime? LastProcessedRepairOrderChangeDateUtc { get; private set; }
    public long? LastProcessedRepairOrderId { get; private set; }
    public string? LastRepairOrderError { get; private set; }
    public int LastRepairOrderBatchProcessedCount { get; private set; }

    // Repair Order Line Item sync state
    public DateTime? LastSuccessfulRepairOrderLineItemPollUtc { get; private set; }
    public DateTime? LastProcessedRepairOrderLineItemChangeDateUtc { get; private set; }
    public long? LastProcessedRepairOrderLineItemId { get; private set; }
    public string? LastRepairOrderLineItemError { get; private set; }
    public int LastRepairOrderLineItemBatchProcessedCount { get; private set; }

    // GL Account sync state
    public DateTime? LastSuccessfulGLAccountPollUtc { get; private set; }
    public string? LastGLAccountError { get; private set; }
    public int LastGLAccountProcessedCount { get; private set; }
    public long[]? LastSyncedGLAccountIds { get; private set; }

    // PO sync state
    public DateTime? LastSuccessfulPOPollUtc { get; private set; }
    public DateTime? LastProcessedPOChangeDateUtc { get; private set; }
    public long? LastProcessedPOMasterId { get; private set; }
    public DateTime? LastProcessedPODetailChangeDateUtc { get; private set; }
    public long? LastProcessedPODetailId { get; private set; }
    public string? LastPOError { get; private set; }
    public int LastPOHeadersProcessedCount { get; private set; }
    public int LastPODetailProcessedCount { get; private set; }

    public SyncState()
    {
        Load();
    }

    public void MarkSuccess(DateTime? lastChangeDateUtc, long? lastCustomerId, int batchCount)
    {
        lock (_lock)
        {
            LastSuccessfulPollUtc = DateTime.UtcNow;
            LastProcessedChangeDateUtc = lastChangeDateUtc;
            LastProcessedCustomerId = lastCustomerId;
            LastBatchProcessedCount = batchCount;
            LastError = null;
            Save();
        }
    }

    public void MarkVendorSuccess(DateTime? lastChangeDateUtc, long? lastVendorId, int batchCount)
    {
        lock (_lock)
        {
            LastSuccessfulVendorPollUtc = DateTime.UtcNow;
            LastProcessedVendorChangeDateUtc = lastChangeDateUtc;
            LastProcessedVendorId = lastVendorId;
            LastVendorBatchProcessedCount = batchCount;
            LastVendorError = null;
            Save();
        }
    }

    public void MarkGLAccountSuccess(int processedCount, long[] syncedGLAccountIds)
    {
        lock (_lock)
        {
            LastSuccessfulGLAccountPollUtc = DateTime.UtcNow;
            LastGLAccountProcessedCount = processedCount;
            LastSyncedGLAccountIds = syncedGLAccountIds;
            LastGLAccountError = null;
            Save();
        }
    }

    /// <summary>Records this entity's poll error, or clears it when passed null.</summary>
    public void MarkGLAccountError(string? error)
    {
        lock (_lock)
        {
            if (LastGLAccountError == error) return;
            LastGLAccountError = error;
            Save();
        }
    }

    public void MarkPOSuccess(DateTime? lastChangeDateUtc, long? lastPOMasterId, int headersCount, int detailsCount)
    {
        lock (_lock)
        {
            LastSuccessfulPOPollUtc = DateTime.UtcNow;
            LastProcessedPOChangeDateUtc = lastChangeDateUtc;
            LastProcessedPOMasterId = lastPOMasterId;
            LastPOHeadersProcessedCount = headersCount;
            LastPODetailProcessedCount = detailsCount;
            LastPOError = null;
            Save();
        }
    }

    public void MarkPODetailSuccess(DateTime? lastChangeDateUtc, long? lastPODetailId)
    {
        lock (_lock)
        {
            LastProcessedPODetailChangeDateUtc = lastChangeDateUtc;
            LastProcessedPODetailId = lastPODetailId;
            Save();
        }
    }

    /// <summary>Records this entity's poll error, or clears it when passed null.</summary>
    public void MarkError(string? error)
    {
        lock (_lock)
        {
            if (LastError == error) return;
            LastError = error;
            Save();
        }
    }

    /// <summary>Records this entity's poll error, or clears it when passed null.</summary>
    public void MarkVendorError(string? error)
    {
        lock (_lock)
        {
            if (LastVendorError == error) return;
            LastVendorError = error;
            Save();
        }
    }

    public void MarkPartSuccess(DateTime? lastChangeDateUtc, long? lastPartId, int batchCount)
    {
        lock (_lock)
        {
            LastSuccessfulPartPollUtc = DateTime.UtcNow;
            LastProcessedPartChangeDateUtc = lastChangeDateUtc;
            LastProcessedPartId = lastPartId;
            LastPartBatchProcessedCount = batchCount;
            LastPartError = null;
            Save();
        }
    }

    /// <summary>Records this entity's poll error, or clears it when passed null.</summary>
    public void MarkPartError(string? error)
    {
        lock (_lock)
        {
            if (LastPartError == error) return;
            LastPartError = error;
            Save();
        }
    }

    public void MarkRepairOrderSuccess(DateTime? lastChangeDateUtc, long? lastRepairOrderId, int batchCount)
    {
        lock (_lock)
        {
            LastSuccessfulRepairOrderPollUtc = DateTime.UtcNow;
            LastProcessedRepairOrderChangeDateUtc = lastChangeDateUtc;
            LastProcessedRepairOrderId = lastRepairOrderId;
            LastRepairOrderBatchProcessedCount = batchCount;
            LastRepairOrderError = null;
            Save();
        }
    }

    public void MarkRepairOrderLineItemSuccess(DateTime? lastChangeDateUtc, long? lastLineItemId, int batchCount)
    {
        lock (_lock)
        {
            LastSuccessfulRepairOrderLineItemPollUtc = DateTime.UtcNow;
            LastProcessedRepairOrderLineItemChangeDateUtc = lastChangeDateUtc;
            LastProcessedRepairOrderLineItemId = lastLineItemId;
            LastRepairOrderLineItemBatchProcessedCount = batchCount;
            LastRepairOrderLineItemError = null;
            Save();
        }
    }

    /// <summary>Records this entity's poll error, or clears it when passed null.</summary>
    public void MarkRepairOrderError(string? error)
    {
        lock (_lock)
        {
            if (LastRepairOrderError == error) return;
            LastRepairOrderError = error;
            Save();
        }
    }

    /// <summary>Records this entity's poll error, or clears it when passed null.</summary>
    public void MarkRepairOrderLineItemError(string? error)
    {
        lock (_lock)
        {
            if (LastRepairOrderLineItemError == error) return;
            LastRepairOrderLineItemError = error;
            Save();
        }
    }

    /// <summary>Records this entity's poll error, or clears it when passed null.</summary>
    public void MarkPOError(string? error)
    {
        lock (_lock)
        {
            if (LastPOError == error) return;
            LastPOError = error;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StateFilePath))
            {
                return;
            }

            string json = File.ReadAllText(StateFilePath);
            var state = System.Text.Json.JsonSerializer.Deserialize<SyncStateData>(json);
            if (state != null)
            {
                LastSuccessfulPollUtc = state.LastSuccessfulPollUtc;
                LastProcessedChangeDateUtc = state.LastProcessedChangeDateUtc;
                LastProcessedCustomerId = state.LastProcessedCustomerId;
                LastError = state.LastError;
                LastBatchProcessedCount = state.LastBatchProcessedCount;

                LastSuccessfulVendorPollUtc = state.LastSuccessfulVendorPollUtc;
                LastProcessedVendorChangeDateUtc = state.LastProcessedVendorChangeDateUtc;
                LastProcessedVendorId = state.LastProcessedVendorId;
                LastVendorError = state.LastVendorError;
                LastVendorBatchProcessedCount = state.LastVendorBatchProcessedCount;

                LastSuccessfulPartPollUtc = state.LastSuccessfulPartPollUtc;
                LastProcessedPartChangeDateUtc = state.LastProcessedPartChangeDateUtc;
                LastProcessedPartId = state.LastProcessedPartId;
                LastPartError = state.LastPartError;
                LastPartBatchProcessedCount = state.LastPartBatchProcessedCount;

                LastSuccessfulRepairOrderPollUtc = state.LastSuccessfulRepairOrderPollUtc;
                LastProcessedRepairOrderChangeDateUtc = state.LastProcessedRepairOrderChangeDateUtc;
                LastProcessedRepairOrderId = state.LastProcessedRepairOrderId;
                LastRepairOrderError = state.LastRepairOrderError;
                LastRepairOrderBatchProcessedCount = state.LastRepairOrderBatchProcessedCount;

                LastSuccessfulRepairOrderLineItemPollUtc = state.LastSuccessfulRepairOrderLineItemPollUtc;
                LastProcessedRepairOrderLineItemChangeDateUtc = state.LastProcessedRepairOrderLineItemChangeDateUtc;
                LastProcessedRepairOrderLineItemId = state.LastProcessedRepairOrderLineItemId;
                LastRepairOrderLineItemError = state.LastRepairOrderLineItemError;
                LastRepairOrderLineItemBatchProcessedCount = state.LastRepairOrderLineItemBatchProcessedCount;

                LastSuccessfulGLAccountPollUtc = state.LastSuccessfulGLAccountPollUtc;
                LastGLAccountError = state.LastGLAccountError;
                LastGLAccountProcessedCount = state.LastGLAccountProcessedCount;
                LastSyncedGLAccountIds = state.LastSyncedGLAccountIds;

                LastSuccessfulPOPollUtc = state.LastSuccessfulPOPollUtc;
                LastProcessedPOChangeDateUtc = state.LastProcessedPOChangeDateUtc;
                LastProcessedPOMasterId = state.LastProcessedPOMasterId;
                LastProcessedPODetailChangeDateUtc = state.LastProcessedPODetailChangeDateUtc;
                LastProcessedPODetailId = state.LastProcessedPODetailId;
                LastPOError = state.LastPOError;
                LastPOHeadersProcessedCount = state.LastPOHeadersProcessedCount;
                LastPODetailProcessedCount = state.LastPODetailProcessedCount;
            }
        }
        catch
        {
            // If load fails, start fresh
        }
    }

    private void Save()
    {
        try
        {
            var state = new SyncStateData
            {
                LastSuccessfulPollUtc = LastSuccessfulPollUtc,
                LastProcessedChangeDateUtc = LastProcessedChangeDateUtc,
                LastProcessedCustomerId = LastProcessedCustomerId,
                LastError = LastError,
                LastBatchProcessedCount = LastBatchProcessedCount,

                LastSuccessfulVendorPollUtc = LastSuccessfulVendorPollUtc,
                LastProcessedVendorChangeDateUtc = LastProcessedVendorChangeDateUtc,
                LastProcessedVendorId = LastProcessedVendorId,
                LastVendorError = LastVendorError,
                LastVendorBatchProcessedCount = LastVendorBatchProcessedCount,

                LastSuccessfulPartPollUtc = LastSuccessfulPartPollUtc,
                LastProcessedPartChangeDateUtc = LastProcessedPartChangeDateUtc,
                LastProcessedPartId = LastProcessedPartId,
                LastPartError = LastPartError,
                LastPartBatchProcessedCount = LastPartBatchProcessedCount,

                LastSuccessfulRepairOrderPollUtc = LastSuccessfulRepairOrderPollUtc,
                LastProcessedRepairOrderChangeDateUtc = LastProcessedRepairOrderChangeDateUtc,
                LastProcessedRepairOrderId = LastProcessedRepairOrderId,
                LastRepairOrderError = LastRepairOrderError,
                LastRepairOrderBatchProcessedCount = LastRepairOrderBatchProcessedCount,

                LastSuccessfulRepairOrderLineItemPollUtc = LastSuccessfulRepairOrderLineItemPollUtc,
                LastProcessedRepairOrderLineItemChangeDateUtc = LastProcessedRepairOrderLineItemChangeDateUtc,
                LastProcessedRepairOrderLineItemId = LastProcessedRepairOrderLineItemId,
                LastRepairOrderLineItemError = LastRepairOrderLineItemError,
                LastRepairOrderLineItemBatchProcessedCount = LastRepairOrderLineItemBatchProcessedCount,

                LastSuccessfulGLAccountPollUtc = LastSuccessfulGLAccountPollUtc,
                LastGLAccountError = LastGLAccountError,
                LastGLAccountProcessedCount = LastGLAccountProcessedCount,
                LastSyncedGLAccountIds = LastSyncedGLAccountIds,

                LastSuccessfulPOPollUtc = LastSuccessfulPOPollUtc,
                LastProcessedPOChangeDateUtc = LastProcessedPOChangeDateUtc,
                LastProcessedPOMasterId = LastProcessedPOMasterId,
                LastProcessedPODetailChangeDateUtc = LastProcessedPODetailChangeDateUtc,
                LastProcessedPODetailId = LastProcessedPODetailId,
                LastPOError = LastPOError,
                LastPOHeadersProcessedCount = LastPOHeadersProcessedCount,
                LastPODetailProcessedCount = LastPODetailProcessedCount
            };

            string directory = Path.GetDirectoryName(StateFilePath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(StateFilePath, json);
        }
        catch
        {
            // If save fails, continue (state will be lost on restart)
        }
    }

    private class SyncStateData
    {
        public DateTime? LastSuccessfulPollUtc { get; set; }
        public DateTime? LastProcessedChangeDateUtc { get; set; }
        public long? LastProcessedCustomerId { get; set; }
        public string? LastError { get; set; }
        public int LastBatchProcessedCount { get; set; }

        public DateTime? LastSuccessfulVendorPollUtc { get; set; }
        public DateTime? LastProcessedVendorChangeDateUtc { get; set; }
        public long? LastProcessedVendorId { get; set; }
        public string? LastVendorError { get; set; }
        public int LastVendorBatchProcessedCount { get; set; }

        public DateTime? LastSuccessfulPartPollUtc { get; set; }
        public DateTime? LastProcessedPartChangeDateUtc { get; set; }
        public long? LastProcessedPartId { get; set; }
        public string? LastPartError { get; set; }
        public int LastPartBatchProcessedCount { get; set; }

        public DateTime? LastSuccessfulRepairOrderPollUtc { get; set; }
        public DateTime? LastProcessedRepairOrderChangeDateUtc { get; set; }
        public long? LastProcessedRepairOrderId { get; set; }
        public string? LastRepairOrderError { get; set; }
        public int LastRepairOrderBatchProcessedCount { get; set; }

        public DateTime? LastSuccessfulRepairOrderLineItemPollUtc { get; set; }
        public DateTime? LastProcessedRepairOrderLineItemChangeDateUtc { get; set; }
        public long? LastProcessedRepairOrderLineItemId { get; set; }
        public string? LastRepairOrderLineItemError { get; set; }
        public int LastRepairOrderLineItemBatchProcessedCount { get; set; }

        public DateTime? LastSuccessfulGLAccountPollUtc { get; set; }
        public string? LastGLAccountError { get; set; }
        public int LastGLAccountProcessedCount { get; set; }
        public long[]? LastSyncedGLAccountIds { get; set; }

        public DateTime? LastSuccessfulPOPollUtc { get; set; }
        public DateTime? LastProcessedPOChangeDateUtc { get; set; }
        public long? LastProcessedPOMasterId { get; set; }
        public DateTime? LastProcessedPODetailChangeDateUtc { get; set; }
        public long? LastProcessedPODetailId { get; set; }
        public string? LastPOError { get; set; }
        public int LastPOHeadersProcessedCount { get; set; }
        public int LastPODetailProcessedCount { get; set; }
    }
}
