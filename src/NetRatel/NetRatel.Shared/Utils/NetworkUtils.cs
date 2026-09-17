using System.Net;
using System.Net.Sockets;

namespace NetRatel.Shared.Utils
{
    public static class NetworkUtils
    {
        public static string GetLocalIPAddress()
        {
            try
            {
                var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
                var ip = hostEntry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                return ip?.ToString() ?? "Not Found";
            }
            catch
            {
                return "Error";
            }
        }
    }
}