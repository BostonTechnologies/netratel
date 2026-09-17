using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetRatel.Shared.Data.Task
{
    public static class TaskStatuses
    {
        public const string New = "New";
        public const string Processing = "Processing"; // Client started processing
        public const string Running = Processing;      // Backwards compatibility alias
        public const string Completed = "Completed";   // Client finished successfully
        public const string Failed = "Failed";         // Client encountered an error
        public const string Cancelled = "Cancelled";   // Client task was cancelled before or during execution
        public const string TimedOut = "TimedOut";     // Client task exceeded its orchestration timeout

        public static bool IsDispatchable(string? status)
            => string.Equals(status, New, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase);

        public static bool IsTerminal(string? status)
            => !IsDispatchable(status) &&
               !string.Equals(status, Processing, StringComparison.OrdinalIgnoreCase);
    }
}
